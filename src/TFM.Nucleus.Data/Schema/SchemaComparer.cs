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
                // Set by an update for a rebuild it has done; a compare starts from the catalog.
                table.RebuildForKeys = false;
                if (!snapshot.Tables.Contains(physical, comparer))
                {
                    table.ExistsInDatabase = false;
                    logger?.LogWarning("Database table schema is not current. '{tablename}' is missing.", table.TableName);
                    continue;
                }
                table.ExistsInDatabase = true;
                var tableColumns = snapshot.Columns.Where(c => comparer.Equals(c.TableName, physical)).ToList();
                // A page with no order is ordered by ClusterID, so a table the engine should give
                // one and that lacks it (a legacy table) is rebuilt with it.
                table.MissingClusterId = dialect.HasClusterIdColumn &&
                    !tableColumns.Any(c => comparer.Equals(c.Name, dialect.PhysicalName(TableSchema.ClusterIdColumn)));
                if (table.MissingClusterId)
                    logger?.LogWarning("Database table schema is not current. '{tablename}' has no {column} column; it will be rebuilt with one.",
                        table.TableName, TableSchema.ClusterIdColumn);
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
            if (table.Columns.Any(c => c.ExistsInDatabase && !c.SchemaMatched) || !table.IdentityColumn.ExistsInDatabase || table.RebuildForKeys
                || table.MissingClusterId)
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
                // A Nucleus index always has exactly one key column, and carries the soft-delete
                // flag or nothing. Anything else with our prefix is stale and gets rebuilt.
                var keys = group.Where(r => !r.IsIncluded).ToList();
                if (col == null || keys.Count != 1 || !ColumnIndexMatches(col, keys[0], dialect)
                    || !CarriesOnly(group.Where(r => r.IsIncluded).Select(r => r.ColumnName), CarriedColumns(table, dialect), dialect))
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
        /// Compares the keys a table needs against the Nucleus-named keys the database has on it.
        /// Returns the physical key names to drop (no longer needed, or a different column, table
        /// or delete action) and the keys to create. An unchecked key that otherwise matches is
        /// kept: checking it again would fail for the same rows.
        /// </summary>
        public static (List<string> drop, List<ForeignKeySchema> create) DiffForeignKeys(
            TableSchema table, IEnumerable<CatalogForeignKey> dbKeys, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var physicalTable = dialect.PhysicalName(table.TableName);
            var existing = dbKeys
                .Where(k => comparer.Equals(k.TableName, physicalTable) && IndexNaming.IsForeignKeyName(k.KeyName))
                .ToList();
            List<string> drop = [];
            List<ForeignKeySchema> create = [];
            foreach (var key in existing)
            {
                var required = table.ForeignKeys.FirstOrDefault(k => comparer.Equals(dialect.PhysicalName(k.Name), key.KeyName));
                if (required == null || !ForeignKeyMatches(required, key, dialect))
                    drop.Add(key.KeyName!);
            }
            foreach (var key in table.ForeignKeys)
            {
                var name = dialect.PhysicalName(key.Name);
                if (!existing.Any(k => comparer.Equals(k.KeyName, name)) || drop.Contains(name, comparer))
                    create.Add(key);
            }
            return (drop, create);
        }

        private static bool ForeignKeyMatches(ForeignKeySchema key, CatalogForeignKey existing, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            return comparer.Equals(existing.ColumnName, key.Column.ColumnName)
                && comparer.Equals(existing.ReferencedTableName, dialect.PhysicalName(key.Referenced.TableName))
                && comparer.Equals(existing.ReferencedColumnName, key.Referenced.IdentityColumn!.ColumnName)
                && existing.OnDelete == key.OnDelete;
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
                if (index == null || !TableIndexMatches(table, index, group, dialect))
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
        private static bool TableIndexMatches(TableSchema table, TableIndexSchema index, IEnumerable<CatalogIndex> columns, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var rows = columns.ToList();
            if (rows.Any(r => r.IsUnique != index.Unique || r.HasFilter != index.Unique || r.IsDescending || (!r.IsIncluded && !r.NullsSortLow)))
                return false;
            var keys = rows.Where(r => !r.IsIncluded).OrderBy(r => r.KeyOrdinal).Select(r => r.ColumnName).ToList();
            var included = rows.Where(r => r.IsIncluded).Select(r => r.ColumnName).ToList();
            if (!keys.SequenceEqual(index.Keys.Select(k => dialect.PhysicalName(k.ColumnName)), comparer))
                return false;
            // An engine without included columns stores the keys alone, so the keys are the whole index.
            if (!dialect.SupportsIncludedColumns)
                return included.Count == 0;
            return CarriesOnly(included, [.. index.Include.Select(c => c.ColumnName), .. CarriedColumns(table, dialect)], dialect);
        }

        private static readonly string[] SoftDeleteFlag = [SoftDelete.IsDeletedColumn];

        /// <summary>
        /// The columns every index of <paramref name="table"/> carries beside its own: the
        /// soft-delete flag, for a soft-deletable table of a dialect that sets
        /// <see cref="ISchemaDialect.IndexesCarrySoftDeleteFlag"/>, and nothing otherwise.
        /// </summary>
        public static IReadOnlyList<string> CarriedColumns(TableSchema table, ISchemaDialect dialect) =>
            dialect.IndexesCarrySoftDeleteFlag && dialect.SupportsIncludedColumns && table.IsSoftDeletable ? SoftDeleteFlag : [];

        /// <summary>Whether the included columns an index has are exactly those it should, in any order.</summary>
        private static bool CarriesOnly(IEnumerable<string?> included, IReadOnlyList<string> expected, ISchemaDialect dialect)
        {
            var comparer = dialect.IdentifierComparer;
            var names = expected.Select(dialect.PhysicalName).Distinct(comparer).ToList();
            var count = 0;
            foreach (var column in included)
            {
                if (column == null || !names.Contains(column, comparer))
                    return false;
                count++;
            }
            return count == names.Count;
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
            existing.IsDescending == col.IndexDescending &&
            existing.NullsSortLow;
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
