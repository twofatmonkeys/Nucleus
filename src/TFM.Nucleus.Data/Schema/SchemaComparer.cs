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
                if (!snapshot.Tables.Contains(physical, comparer))
                {
                    table.ExistsInDatabase = false;
                    logger?.LogWarning("Database table schema is not current. '{tablename}' is missing.", table.TableName);
                    continue;
                }
                table.ExistsInDatabase = true;
                var tableColumns = snapshot.Columns.Where(c => comparer.Equals(c.TableName, physical)).ToList();
                if (table.IsSoftDeletable && !SoftDeleteColumnNames.All(name => tableColumns.Any(c => comparer.Equals(c.Name, name))))
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
                .Where(x => comparer.Equals(x.TableName, physicalTable))
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
}
