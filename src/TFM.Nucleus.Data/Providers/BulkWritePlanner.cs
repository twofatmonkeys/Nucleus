using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>One table's share of a bulk write: all inserts or all updates.</summary>
    public sealed record BulkTableWrite(TableSchema Schema, string TableName, bool IsUpdate, List<Query> Queries);

    /// <summary>A column of a bulk staging table: its name and the CLR type its values are written as.</summary>
    public sealed record BulkColumn(string Name, ColumnSchema Column)
    {
        public Type ClrType => Column.ClrType;
    }

    /// <summary>
    /// What a bulk write reads back: the identity column, the output columns in the order the
    /// statement returns them after the identity, and each written row's output terms by its
    /// identity and column.
    /// </summary>
    public sealed record BulkOutputs(string IdentityColumn, IReadOnlyList<string> Columns, IReadOnlyDictionary<Guid, Dictionary<string, OutputParameter>> ByIdentity)
    {
        /// <summary>A write that reads nothing back.</summary>
        public static readonly BulkOutputs None = new("", [], new Dictionary<Guid, Dictionary<string, OutputParameter>>());

        public bool IsEmpty => Columns.Count == 0;
    }

    /// <summary>
    /// The engine-neutral half of a bulk insert or update: which tables, which columns, and the
    /// staged rows. An insert batch is grouped by the columns each row sets, so a column a row
    /// leaves unset keeps its database default rather than becoming null. An update batch is
    /// staged once, with a bitmask per row naming the columns that row changes, so rows that
    /// change different columns still go in one statement. A provider stages the rows with its
    /// own copy mechanism and writes the statements in its own dialect.
    /// </summary>
    public static class BulkWritePlanner
    {
        /// <summary>
        /// A linked insert or update chain over a schema'd table. Must be called before
        /// NeedsSplitting, which detaches linked queries.
        /// </summary>
        public static bool CanUseBulkPath(Query query) =>
            query.CountLinkedQueries() > 0
            && (query.Type == QueryType.INSERT || query.Type == QueryType.UPDATE)
            && query.TableSchema is TableSchema;

        /// <summary>A SET term evaluated by the server rather than bound, such as the timestamp.</summary>
        public static bool IsServerFunction(IQueryParameter p) => !p.UsesVariable && p.Value != null;

        /// <summary>The query and its linked queries, grouped by table. Each group is all inserts or all updates.</summary>
        public static IReadOnlyList<BulkTableWrite> GroupByTable(Query rootQuery)
        {
            var allQueries = new List<Query>(rootQuery.LinkedQueries.Count + 1) { rootQuery };
            allQueries.AddRange(rootQuery.LinkedQueries);

            // Safety: all queries must have schema for the bulk path
            var queriesWithoutSchema = allQueries.Count(q => q.TableSchema is not TableSchema || q.TableName == null);
            if (queriesWithoutSchema > 0)
                throw new InvalidOperationException(
                    $"{queriesWithoutSchema} of {allQueries.Count} queries are missing TableSchema or TableName. " +
                    $"All queries in a bulk batch must have a TableSchema set.");

            List<BulkTableWrite> writes = [];
            foreach (var tableGroup in allQueries.GroupBy(q => q.TableName!, StringComparer.OrdinalIgnoreCase))
            {
                var queries = tableGroup.ToList();
                // Validate all queries in the group are the same operation type
                if (queries.Any(q => q.Type != queries[0].Type))
                    throw new InvalidOperationException(
                        $"Mixed insert and update operations for table '{tableGroup.Key}' in a single bulk batch. " +
                        $"Split inserts and updates into separate queries.");
                writes.Add(new BulkTableWrite((TableSchema)queries[0].TableSchema!, tableGroup.Key, queries[0].Type == QueryType.UPDATE, queries));
            }
            return writes;
        }

        /// <summary>
        /// Groups queries by their SET column signature (the sorted data column names, server
        /// functions excluded). Queries in the same group set identical columns.
        /// </summary>
        public static List<List<Query>> GroupByColumnSignature(List<Query> queries)
        {
            var groups = new Dictionary<string, List<Query>>(StringComparer.Ordinal);

            foreach (var q in queries)
            {
                var names = q.SetParameters
                    .Where(p => p != null && p.Name != null && !IsServerFunction(p))
                    .Select(p => p!.Name!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

                var signature = string.Join("|", names);

                if (!groups.TryGetValue(signature, out var group))
                {
                    group = [];
                    groups[signature] = group;
                }
                group.Add(q);
            }

            return [.. groups.Values];
        }

        /// <summary>
        /// The union of the SET columns of the queries, split into data columns (staged) and
        /// server function columns (applied by the statement), each in first-seen order.
        /// </summary>
        public static (List<BulkColumn> DataColumns, Dictionary<string, Parameter> ServerFunctionColumns) ScanSetColumns(
            IEnumerable<Query> queries, TableSchema schema)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<BulkColumn> dataColumns = [];
            Dictionary<string, Parameter> serverFunctionColumns = [];

            foreach (var q in queries)
            {
                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null) continue;

                    if (IsServerFunction(p))
                    {
                        serverFunctionColumns.TryAdd(p.Name, p);
                        continue;
                    }

                    if (!seen.Add(p.Name)) continue;

                    var schemaCol = schema.FindByColumnName(p.Name)
                        ?? throw new InvalidOperationException(
                            $"Column '{p.Name}' not found in schema for table '{schema.TableName}'.");
                    dataColumns.Add(new BulkColumn(p.Name, schemaCol));
                }
            }
            return (dataColumns, serverFunctionColumns);
        }

        /// <summary>
        /// The output terms of the queries, keyed by the identity of the row each writes: an
        /// insert carries its identity as a SET term, an update as its WHERE term.
        /// </summary>
        public static BulkOutputs ScanOutputs(IEnumerable<Query> queries, TableSchema schema)
        {
            var identity = schema.IdentityColumn.ColumnName;
            List<string> columns = [];
            Dictionary<Guid, Dictionary<string, OutputParameter>> byIdentity = [];
            foreach (var q in queries)
            {
                var outputs = q.SetParameters.OfType<OutputParameter>().ToList();
                if (outputs.Count == 0)
                    continue;
                var id = q.SetParameters.Concat(q.Where.AllParameters())
                    .FirstOrDefault(p => p != null && string.Equals(p.Name, identity, StringComparison.OrdinalIgnoreCase))?.Value as Guid?
                    ?? throw new InvalidOperationException($"A bulk write to '{schema.TableName}' has a row without its {identity}, so its output cannot be read back.");
                var row = new Dictionary<string, OutputParameter>(StringComparer.OrdinalIgnoreCase);
                foreach (var output in outputs)
                {
                    row[output.Name!] = output;
                    if (!columns.Contains(output.Name!, StringComparer.OrdinalIgnoreCase))
                        columns.Add(output.Name!);
                }
                byIdentity[id] = row;
            }
            return new BulkOutputs(identity, columns, byIdentity);
        }

        /// <summary>The WHERE columns of the queries (the identity column of an update), in first-seen order.</summary>
        public static List<BulkColumn> ScanWhereColumns(IEnumerable<Query> queries, TableSchema schema)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<BulkColumn> whereColumns = [];

            foreach (var q in queries)
            {
                foreach (var p in q.Where.AllParameters())
                {
                    if (p?.Name == null) continue;
                    if (!seen.Add(p.Name)) continue;

                    var schemaCol = schema.FindByColumnName(p.Name)
                        ?? throw new InvalidOperationException(
                            $"Filter column '{p.Name}' not found in schema for table '{schema.TableName}'.");
                    whereColumns.Add(new BulkColumn(p.Name, schemaCol));
                }
            }
            return whereColumns;
        }

        /// <summary>The staged rows of an insert group: one value per data column, null as <see cref="DBNull"/>.</summary>
        public static List<object?[]> InsertRows(IEnumerable<Query> queryGroup, IReadOnlyList<BulkColumn> dataColumns)
        {
            var ordinals = Ordinals(dataColumns.Select(c => c.Name));
            List<object?[]> rows = [];
            foreach (var q in queryGroup)
            {
                var row = NewRow(dataColumns.Count);
                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null || IsServerFunction(p)) continue;
                    if (!ordinals.TryGetValue(p.Name, out var ordinal)) continue;
                    row[ordinal] = CoerceValue(p.Value);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// The staged rows of an update: the data columns, then the WHERE columns, then the
        /// bitmask of the data columns the row changes (bit i of byte i / 8 for data column i).
        /// A column the row does not change stays <see cref="DBNull"/> with its bit clear.
        /// </summary>
        public static List<object?[]> UpdateRows(IEnumerable<Query> queries, IReadOnlyList<BulkColumn> dataColumns, IReadOnlyList<BulkColumn> whereColumns)
        {
            var dataOrdinals = Ordinals(dataColumns.Select(c => c.Name));
            var whereOrdinals = Ordinals(whereColumns.Select(c => c.Name));
            var bitmaskByteCount = BitmaskBytes(dataColumns.Count);
            var width = dataColumns.Count + whereColumns.Count + 1;
            List<object?[]> rows = [];
            foreach (var q in queries)
            {
                var row = NewRow(width);
                var bitmask = new byte[bitmaskByteCount];

                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null || IsServerFunction(p)) continue;
                    if (!dataOrdinals.TryGetValue(p.Name, out int bitIdx)) continue;

                    // Set the bit for this column
                    bitmask[bitIdx / 8] |= (byte)(1 << (bitIdx % 8));
                    // Set the value (works for both normal values and explicit NULLs)
                    row[bitIdx] = CoerceValue(p.Value);
                }

                foreach (var p in q.Where.AllParameters())
                {
                    if (p?.Name == null) continue;
                    if (!whereOrdinals.TryGetValue(p.Name, out var ordinal)) continue;
                    row[dataColumns.Count + ordinal] = CoerceValue(p.Value);
                }

                row[width - 1] = bitmask;
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Bytes of bitmask for a number of data columns.</summary>
        public static int BitmaskBytes(int dataColumnCount) => (dataColumnCount + 7) / 8;

        /// <summary>The staging name of a WHERE column, so it cannot collide with a data column.</summary>
        public static string WhereColumnAlias(string columnName) => $"_w_{columnName}";

        /// <summary>A value as staged: null as <see cref="DBNull"/>, an enum as its int value.</summary>
        public static object CoerceValue(object? value)
        {
            if (value == null)
                return DBNull.Value;
            if (value.GetType().IsEnum)
                return Convert.ToInt32(value);
            return value;
        }

        private static object?[] NewRow(int width)
        {
            var row = new object?[width];
            Array.Fill(row, DBNull.Value);
            return row;
        }

        private static Dictionary<string, int> Ordinals(IEnumerable<string> names)
        {
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var name in names)
                ordinals[name] = i++;
            return ordinals;
        }
    }
}
