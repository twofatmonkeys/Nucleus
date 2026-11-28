using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The table types a long list of values is sent as: one table-valued parameter in place of a
    /// parameter per value, which SQL Server caps at 2,100 a command. Measured on 100,000 rows at
    /// compatibility level 100 (see <c>docs\code-review-findings.md</c>): about 1 ms slower than a
    /// parameter per value up to 200 values, level to 400, then faster (2.3 times at 2,000), and
    /// faster than OPENJSON at every length above that, which also needs level 130. So a list
    /// goes this way only above <see cref="Threshold"/> values, and a shorter one is written as
    /// it always was.
    /// </summary>
    internal static class SqlServerListTypes
    {
        /// <summary>A list of more values than this is one table-valued parameter.</summary>
        public const int Threshold = 200;

        /// <summary>The longest text a text list carries; a list with a longer value is bound a value at a time.</summary>
        private const int MaxTextLength = 4000;

        /// <summary>
        /// One list type. The number types are keyed, which gives the optimiser distinct values in
        /// order; the text type is not, because two values the client holds apart ("a" and "A")
        /// can be one under the database's collation.
        /// </summary>
        internal sealed record ListType(string Name, string ColumnType, SqlMetaData Column, bool Keyed)
        {
            public string QualifiedName => $"[dbo].[{Name}]";

            public string CreateSql =>
                $"CREATE TYPE {QualifiedName} AS TABLE ([Value] {ColumnType} NOT NULL{(Keyed ? " PRIMARY KEY" : "")})";
        }

        private static readonly ListType GuidList = new("NucleusGuidList", "uniqueidentifier", new SqlMetaData("Value", SqlDbType.UniqueIdentifier), true);
        private static readonly ListType IntList = new("NucleusIntList", "int", new SqlMetaData("Value", SqlDbType.Int), true);
        private static readonly ListType BigIntList = new("NucleusBigIntList", "bigint", new SqlMetaData("Value", SqlDbType.BigInt), true);
        private static readonly ListType TextList = new("NucleusTextList", $"nvarchar({MaxTextLength})", new SqlMetaData("Value", SqlDbType.NVarChar, MaxTextLength), false);

        public static readonly IReadOnlyList<ListType> All = [GuidList, IntList, BigIntList, TextList];

        /// <summary>The list type for values of <paramref name="elementType"/>, an enum going as its number; null when there is none.</summary>
        private static ListType? For(Type elementType)
        {
            if (elementType.IsEnum)
                elementType = Enum.GetUnderlyingType(elementType);
            if (elementType == typeof(Guid))
                return GuidList;
            if (elementType == typeof(int) || elementType == typeof(short) || elementType == typeof(byte))
                return IntList;
            if (elementType == typeof(long))
                return BigIntList;
            return elementType == typeof(string) ? TextList : null;
        }

        /// <summary>The list type a multi-valued term is sent as, or null when it is written a parameter per value.</summary>
        public static ListType? For(IQueryParameter term)
        {
            if (!ParameterBinding.IsMultiValued(term))
                return null;
            var values = ParameterBinding.Values(term);
            if (values.Count <= Threshold || values.ElementType is not { } elementType || For(elementType) is not { } type)
                return null;
            if (type == TextList)
                foreach (var value in values)
                    if (((string)value).Length > MaxTextLength)
                        return null;
            return type;
        }

        /// <summary>The term's values as one table-valued parameter of <paramref name="type"/>.</summary>
        public static SqlParameter Parameter(string name, ValueList values, ListType type) =>
            new($"@{name}", SqlDbType.Structured) { TypeName = type.QualifiedName, Value = new Records(values, type) };

        /// <summary>
        /// The rows of a table-valued parameter, streamed as the command is sent. One record is
        /// reused for every row, which the driver allows, and each run of the command (a retry
        /// sends it again) reads the list from its start.
        /// </summary>
        private sealed class Records(ValueList values, ListType type) : IEnumerable<SqlDataRecord>
        {
            public IEnumerator<SqlDataRecord> GetEnumerator()
            {
                var record = new SqlDataRecord(type.Column);
                foreach (var value in values)
                {
                    switch (type.Column.SqlDbType)
                    {
                        case SqlDbType.UniqueIdentifier: record.SetGuid(0, (Guid)value); break;
                        case SqlDbType.Int: record.SetInt32(0, Convert.ToInt32(value)); break;
                        case SqlDbType.BigInt: record.SetInt64(0, Convert.ToInt64(value)); break;
                        default: record.SetString(0, (string)value); break;
                    }
                    yield return record;
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>Keeps the list types in the database. A database without them is not current.</summary>
    internal sealed class SqlServerListTypeExtras(SqlServerCatalogReader catalog, ILogger? logger) : ISchemaExtras
    {
        public async ValueTask<bool> AreCurrentAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var missing = await MissingAsync(async, cancellationToken);
            if (missing.Count == 0)
                return true;
            logger?.LogWarning("The list types are not current. Missing: {types}. A long list of values in a query is sent as one of them: {statements}",
                string.Join(", ", missing.Select(t => t.QualifiedName)), string.Join("; ", missing.Select(t => t.CreateSql)));
            return false;
        }

        public async ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            foreach (var type in await MissingAsync(async, cancellationToken))
                await catalog.ExecuteAsync(type.CreateSql, async, cancellationToken);
        }

        private async ValueTask<List<SqlServerListTypes.ListType>> MissingAsync(bool async, CancellationToken cancellationToken)
        {
            var existing = await catalog.ReadTableTypesAsync(async, cancellationToken);
            return [.. SqlServerListTypes.All.Where(t => !existing.Contains(t.Name, StringComparer.OrdinalIgnoreCase))];
        }
    }

    /// <summary>Every kind of extra the SQL Server provider keeps, checked and synced in order.</summary>
    internal sealed class SqlServerExtras(params ISchemaExtras[] parts) : ISchemaExtras
    {
        public async ValueTask<bool> AreCurrentAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            // Every part is asked, so every one that is behind is logged, not only the first.
            var current = true;
            foreach (var part in parts)
                current &= await part.AreCurrentAsync(tables, async, cancellationToken);
            return current;
        }

        public async ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            foreach (var part in parts)
                await part.SyncAsync(tables, async, cancellationToken);
        }
    }
}
