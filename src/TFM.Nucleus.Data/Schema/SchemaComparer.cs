using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// Compares the tables the entity model needs with what a catalog holds. The schema check
    /// and the schema sync both use this, so they cannot disagree.
    /// </summary>
    public static class SchemaComparer
    {
        private static readonly string[] SoftDeleteColumnNames = [SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn];

        /// <summary>
        /// Marks each table and column with whether the database has it and whether it matches.
        /// True when every table and column is current and every soft-deletable table carries
        /// its system columns.
        /// </summary>
        public static bool CompareTables(IEnumerable<TableSchema> tables, CatalogSnapshot snapshot, ISchemaDialect dialect, ILogger? logger)
        {
            var comparer = dialect.IdentifierComparer;
            var softDeleteColumnsCurrent = true;
            var all = tables.ToList();
            foreach (var table in all)
            {
                var physical = dialect.PhysicalName(table.TableName);
                table.MissingSoftDeleteColumns.Clear();
                if (!snapshot.Tables.Contains(physical, comparer))
                {
                    table.ExistsInDatabase = false;
                    logger?.LogWarning("Database table schema is not current. '{tablename}' is missing.", table.TableName);
                    continue;
                }
                table.ExistsInDatabase = true;
                var tableColumns = snapshot.Columns.Where(c => comparer.Equals(c.TableName, physical)).ToList();
                if (table.IsSoftDeletable)
                    table.MissingSoftDeleteColumns.AddRange(SoftDeleteColumnNames.Where(name => !tableColumns.Any(c => comparer.Equals(c.Name, dialect.PhysicalName(name)))));
                if (table.MissingSoftDeleteColumns.Count > 0)
                {
                    softDeleteColumnsCurrent = false;
                    logger?.LogWarning("Soft-delete system columns are missing from table: {tablename}", table.TableName);
                }
                foreach (var schemaColumn in table.Columns)
                {
                    var physicalColumn = dialect.PhysicalName(schemaColumn.ColumnName);
                    var dbColumn = tableColumns.FirstOrDefault(c => comparer.Equals(c.Name, physicalColumn));
                    if (dbColumn == null)
                    {
                        schemaColumn.ExistsInDatabase = false;
                        logger?.LogWarning("Database Column schema is not current for column: {columnname} in table: {tablename}", schemaColumn.ColumnName, table.TableName);
                    }
                    else
                    {
                        schemaColumn.ExistsInDatabase = true;
                        schemaColumn.SchemaMatched = dialect.ColumnMatches(schemaColumn, dbColumn);
                        if (!schemaColumn.SchemaMatched)
                            logger?.LogWarning("Database Column schema is not current for column: {columnname} in table: {tablename}", schemaColumn.ColumnName, table.TableName);
                    }
                }
            }
            return all.All(t => t.IsCurrent) && softDeleteColumnsCurrent;
        }

        /// <summary>
        /// What bringing one compared table in line takes. A table that only lacks columns has
        /// them added in place, which leaves its rows alone and takes no time at any size. A
        /// column of the wrong type, or a missing primary key column, means a rebuild. A column
        /// the entity no longer has is left in the table either way. Call after
        /// <see cref="CompareTables"/>.
        /// </summary>
        internal static TableUpdatePlan PlanUpdate(TableSchema table)
        {
            if (!table.ExistsInDatabase)
                return new TableUpdatePlan(TableUpdateKind.Create, [], []);
            if (table.Columns.Any(c => c.ExistsInDatabase && !c.SchemaMatched) || !table.IdentityColumn.ExistsInDatabase)
                return new TableUpdatePlan(TableUpdateKind.Rebuild, [], []);
            return new TableUpdatePlan(TableUpdateKind.AddColumns,
                [.. table.Columns.Where(c => !c.ExistsInDatabase)],
                [.. table.MissingSoftDeleteColumns]);
        }

        /// <summary>
        /// Compares the column indexes a table needs against the Nucleus-named indexes the
        /// database has. Returns the physical index names to drop and the columns whose index
        /// must be created.
        /// </summary>
        public static (List<string> drop, List<ColumnSchema> create) DiffColumnIndexes(
            TableSchema table, IEnumerable<CatalogIndex> dbIndexes, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var physicalTable = dialect.PhysicalName(table.TableName);
            var existing = dbIndexes
                .Where(x => comparer.Equals(x.TableName, physicalTable) && !IndexNaming.IsTableIndexName(x.IndexName))
                .GroupBy(x => x.IndexName!, comparer)
                .ToList();
            var required = table.IndexedColumns.ToList();
            List<string> drop = [];
            List<ColumnSchema> create = [];

            foreach (var group in existing)
            {
                var col = required.FirstOrDefault(c => comparer.Equals(PhysicalIndexName(table, c, dialect), group.Key));
                // A Nucleus index always has exactly one key column. Anything else with our prefix
                // is stale and gets rebuilt.
                if (col == null || group.Count() != 1 || !ColumnIndexMatches(col, group.First(), dialect))
                    drop.Add(group.Key);
            }
            foreach (var col in required)
            {
                var name = PhysicalIndexName(table, col, dialect);
                if (!existing.Any(g => comparer.Equals(g.Key, name)) || drop.Contains(name, comparer))
                    create.Add(col);
            }
            return (drop, create);
        }

        /// <summary>
        /// Compares the [TableIndex] indexes a table declares against the NIX_ indexes the database
        /// has. An index whose keys, key order, included columns or uniqueness differ is dropped and
        /// created again; one no longer declared is dropped.
        /// </summary>
        public static (List<string> drop, List<TableIndexSchema> create) DiffTableIndexes(
            TableSchema table, IEnumerable<CatalogIndex> dbIndexes, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var physicalTable = dialect.PhysicalName(table.TableName);
            var existing = dbIndexes
                .Where(x => comparer.Equals(x.TableName, physicalTable) && IndexNaming.IsTableIndexName(x.IndexName))
                .GroupBy(x => x.IndexName!, comparer)
                .ToList();
            List<string> drop = [];
            List<TableIndexSchema> create = [];

            foreach (var group in existing)
            {
                var index = table.TableIndexes.FirstOrDefault(i => comparer.Equals(PhysicalIndexName(table, i, dialect), group.Key));
                if (index == null || !TableIndexMatches(index, group, dialect))
                    drop.Add(group.Key);
            }
            foreach (var index in table.TableIndexes)
            {
                var name = PhysicalIndexName(table, index, dialect);
                if (!existing.Any(g => comparer.Equals(g.Key, name)) || drop.Contains(name, comparer))
                    create.Add(index);
            }
            return (drop, create);
        }

        /// <summary>The name the engine stores for a [TableIndex] index.</summary>
        public static string PhysicalIndexName(TableSchema table, TableIndexSchema index, ISchemaDialect dialect) =>
            dialect.PhysicalName(IndexNaming.TableIndexName(table, index));

        // The keys in order, all ascending, then the included columns in any order. A unique index
        // is filtered to rows whose keys are all non-null, so a matching unique index has a filter
        // and a plain one has none, as for a column's index.
        private static bool TableIndexMatches(TableIndexSchema index, IEnumerable<CatalogIndex> columns, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var rows = columns.ToList();
            if (rows.Any(r => r.IsUnique != index.Unique || r.HasFilter != index.Unique || r.IsDescending))
                return false;
            var keys = rows.Where(r => !r.IsIncluded).OrderBy(r => r.KeyOrdinal).Select(r => r.ColumnName).ToList();
            var included = rows.Where(r => r.IsIncluded).Select(r => r.ColumnName).ToList();
            return keys.SequenceEqual(index.Keys.Select(k => dialect.PhysicalName(k.ColumnName)), comparer) &&
                   included.Count == index.Include.Count &&
                   index.Include.All(c => included.Contains(dialect.PhysicalName(c.ColumnName), comparer));
        }

        /// <summary>The name the engine stores for a column's index.</summary>
        public static string PhysicalIndexName(TableSchema table, ColumnSchema column, ISchemaDialect dialect) =>
            dialect.PhysicalName(IndexNaming.ColumnIndexName(table, column));

        // A unique index is filtered to non-null keys, so a matching unique index has a filter
        // and a plain one has none.
        private static bool ColumnIndexMatches(ColumnSchema col, CatalogIndex existing, ISchemaDialect dialect) =>
            dialect.IdentifierComparer.Equals(existing.ColumnName, dialect.PhysicalName(col.ColumnName)) &&
            existing.IsUnique == col.Unique &&
            existing.HasFilter == col.Unique &&
            existing.IsDescending == col.IndexSortDescending;
    }

    internal enum TableUpdateKind
    {
        /// <summary>The table does not exist and is created.</summary>
        Create,

        /// <summary>The table only lacks columns, which are added in place.</summary>
        AddColumns,

        /// <summary>A column changed type, so the table is recreated and its rows copied.</summary>
        Rebuild,
    }

    /// <summary>
    /// How one table is brought in line: for <see cref="TableUpdateKind.AddColumns"/>, the entity
    /// columns and the soft-delete system columns the table lacks.
    /// </summary>
    internal sealed record TableUpdatePlan(TableUpdateKind Kind, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<string> SoftDeleteColumns);
}
