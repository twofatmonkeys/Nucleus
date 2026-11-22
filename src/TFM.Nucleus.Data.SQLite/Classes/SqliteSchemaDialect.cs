using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using static TFM.Nucleus.Data.SQLite.SqliteIdentifiers;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// Every DDL string the SQLite provider runs. A table has the Guid primary key every entity
    /// carries, a column per [DatabaseColumn] property, the soft-delete columns where the type
    /// needs them, and an index per Indexed or Unique column. There is no ClusterID column: SQLite
    /// has no auto-increment column beside the key, and every table already keeps its insertion
    /// order in its rowid, which a rebuild's copy orders by.
    /// <para>
    /// SQLite's DDL is transactional, so a rebuild commits or rolls back whole. It cannot change a
    /// column's type, so any change but added columns rebuilds the table. Its indexes cannot carry
    /// included columns, so a [TableIndex] is created over its keys only.
    /// </para>
    /// </summary>
    internal sealed class SqliteSchemaDialect : ISchemaDialect
    {
        public StringComparer IdentifierComparer => SqliteIdentifiers.Comparer;

        /// <summary>SQLite keeps an identifier of any length, so a name is stored as it is.</summary>
        public string PhysicalName(string logicalName) => logicalName;

        public bool ColumnMatches(ColumnSchema column, CatalogColumn existing) => SqliteTypes.Matches(column, existing);

        public bool SupportsIncludedColumns => false;

        public bool SupportsForeignKeys => true;

        /// <summary>
        /// SQLite cannot add or drop a key on its own: a key is part of its column's definition, so
        /// a table whose keys change is rebuilt.
        /// </summary>
        public bool ForeignKeysInline => true;

        public ISchemaExtras? Extras => null;

        public IReadOnlyList<SchemaStatement> CreateTable(TableSchema table)
        {
            var idColumn = table.IdentityColumn;
            var sb = new StringBuilder($"CREATE TABLE {Quote(table.TableName)} (");
            sb.Append($"{Quote(idColumn.ColumnName)} {SqliteTypes.TypeName(idColumn)} NOT NULL, ");
            foreach (var col in table.Columns)
                if (col.ColumnName != idColumn.ColumnName)
                    sb.Append($"{ColumnDefinition(table, col)}, ");
            if (table.IsSoftDeletable)
                sb.Append($"{SoftDeleteColumnDefinition(SoftDelete.IsDeletedColumn)}, {SoftDeleteColumnDefinition(SoftDelete.DeletedAtColumn)}, ");
            // The key is not named: SQLite keeps a constraint's name only in the CREATE TABLE text,
            // which a rename leaves as it was, while the key's own index follows the table.
            sb.Append($"PRIMARY KEY ({Quote(idColumn.ColumnName)}))");

            List<SchemaStatement> statements = [sb.ToString()];
            foreach (var col in table.IndexedColumns)
                statements.Add(CreateIndex(table, col));
            return statements;
        }

        /// <summary>
        /// An entity column, as CREATE TABLE and ALTER TABLE ADD COLUMN both write it, with its
        /// foreign key when it has one. Every column is created NULL. The key is not named, for
        /// the reason the primary key is not.
        /// </summary>
        private static string ColumnDefinition(TableSchema table, ColumnSchema column)
        {
            var definition = $"{Quote(column.ColumnName)} {SqliteTypes.TypeName(column)} NULL";
            if (!column.HasForeignKey)
                return definition;
            foreach (var key in table.ForeignKeys)
                if (key.Column == column)
                    return $"{definition} REFERENCES {Quote(key.Referenced.TableName)} ({Quote(key.Referenced.IdentityColumn!.ColumnName)})" +
                           (key.OnDelete == ForeignKeyAction.SetNull ? " ON DELETE SET NULL" : "");
            return definition;
        }

        public SchemaStatement CountOrphans(ForeignKeySchema key) =>
            $"SELECT count(*) FROM {Quote(key.Table.TableName)} c WHERE c.{Quote(key.Column.ColumnName)} IS NOT NULL " +
            $"AND NOT EXISTS (SELECT 1 FROM {Quote(key.Referenced.TableName)} r WHERE r.{Quote(key.Referenced.IdentityColumn!.ColumnName)} = c.{Quote(key.Column.ColumnName)})";

        /// <summary>One soft-delete system column, as CREATE TABLE and ALTER TABLE ADD COLUMN both write it.</summary>
        private static string SoftDeleteColumnDefinition(string column) => column switch
        {
            SoftDelete.IsDeletedColumn => $"{Quote(SoftDelete.IsDeletedColumn)} BOOLEAN NOT NULL DEFAULT 0",
            SoftDelete.DeletedAtColumn => $"{Quote(SoftDelete.DeletedAtColumn)} DATETIMEOFFSET NULL",
            _ => throw new ArgumentException($"'{column}' is not a soft-delete system column.", nameof(column)),
        };

        /// <summary>
        /// Adds columns to an existing table, one statement per column, which is all SQLite's
        /// ALTER TABLE takes. Adding a NULL column, or a NOT NULL one with a constant default such
        /// as IsDeleted, only changes the schema, at any table size.
        /// </summary>
        public IReadOnlyList<SchemaStatement>? AddColumns(TableSchema table, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> softDeleteColumns)
        {
            if (columns.Count == 0 && softDeleteColumns.Count == 0)
                throw new ArgumentException("There are no columns to add.", nameof(columns));
            return [.. columns.Select(c => ColumnDefinition(table, c)).Concat(softDeleteColumns.Select(SoftDeleteColumnDefinition))
                .Select(d => (SchemaStatement)$"ALTER TABLE {Quote(table.TableName)} ADD COLUMN {d}")];
        }

        /// <summary>
        /// Renames the table, then drops from it every index the new table will create. SQLite's
        /// index names are unique across the database and cannot be renamed, so the new table could
        /// not take them otherwise. Any other index goes with the old table when it is dropped.
        /// </summary>
        public IReadOnlyList<SchemaStatement> RenameForRebuild(TableSchema table, string tempTableName)
        {
            List<SchemaStatement> statements = [$"ALTER TABLE {Quote(table.TableName)} RENAME TO {Quote(tempTableName)}"];
            foreach (var column in table.IndexedColumns)
                statements.Add($"DROP INDEX IF EXISTS {Quote(IndexNaming.ColumnIndexName(table, column))}");
            foreach (var index in table.TableIndexes)
                statements.Add($"DROP INDEX IF EXISTS {Quote(IndexNaming.TableIndexName(table, index))}");
            return statements;
        }

        /// <summary>
        /// Renames the legacy table. Its indexes keep their old names, which the index sync drops
        /// as names no column needs, before it creates them under the new names; SQLite cannot
        /// rename an index. The primary key's index is SQLite's own and follows the table.
        /// </summary>
        public IReadOnlyList<SchemaStatement> AdoptTable(string legacyTableName, TableSchema table) =>
            [$"ALTER TABLE {Quote(legacyTableName)} RENAME TO {Quote(table.TableName)}"];

        /// <summary>
        /// Copies the rows in the order they were written, which the source's rowid keeps whatever
        /// <paramref name="orderByClusterId"/> says: a SQLite table never has a cluster ID column.
        /// </summary>
        public SchemaStatement CopyRows(string sourceTableName, string destTableName, IReadOnlyList<string> columns, bool orderByClusterId)
        {
            var list = string.Join(", ", columns.Select(Quote));
            return $"INSERT INTO {Quote(destTableName)} ({list}) SELECT {list} FROM {Quote(sourceTableName)} ORDER BY rowid";
        }

        public SchemaStatement DropTable(string tableName) => $"DROP TABLE {Quote(tableName)}";

        public SchemaStatement BackfillTimestamp(TableSchema table) =>
            $"UPDATE {Quote(table.TableName)} SET {Quote(TableSchema.TimestampColumn)} = {SqliteFunctions.NowOffset}() WHERE {Quote(TableSchema.TimestampColumn)} IS NULL";

        /// <summary>
        /// A plain or unique index on one column. A unique index is filtered to non-null keys, as on
        /// the other engines, so the schema check reads the same shape back on every one.
        /// </summary>
        public SchemaStatement CreateIndex(TableSchema table, ColumnSchema column)
        {
            if (!column.HasColumnIndex)
                throw new ArgumentException($"Column '{column.ColumnName}' does not carry a column index.", nameof(column));
            var name = Quote(IndexNaming.ColumnIndexName(table, column));
            var key = $"({Quote(column.ColumnName)} {(column.IndexDescending ? "DESC" : "ASC")})";
            return column.Unique
                ? $"CREATE UNIQUE INDEX {name} ON {Quote(table.TableName)} {key} WHERE {Quote(column.ColumnName)} IS NOT NULL"
                : $"CREATE INDEX {name} ON {Quote(table.TableName)} {key}";
        }

        public SchemaStatement DropIndex(TableSchema table, string physicalIndexName) => $"DROP INDEX {Quote(physicalIndexName)}";

        /// <summary>
        /// A [TableIndex] index over its keys, ascending. Its included columns are left out: SQLite
        /// has no INCLUDE, and adding them as keys would change what a unique index enforces. A
        /// unique one is filtered to rows whose keys are all non-null, as on the other engines.
        /// </summary>
        public SchemaStatement CreateTableIndex(TableSchema table, TableIndexSchema index)
        {
            var keys = string.Join(", ", index.Keys.Select(k => $"{Quote(k.ColumnName)} ASC"));
            var target = $"{Quote(IndexNaming.TableIndexName(table, index))} ON {Quote(table.TableName)} ({keys})";
            return index.Unique
                ? $"CREATE UNIQUE INDEX {target} WHERE {string.Join(" AND ", index.Keys.Select(k => $"{Quote(k.ColumnName)} IS NOT NULL"))}"
                : $"CREATE INDEX {target}";
        }

        public bool IsDuplicateKeyOnIndexBuild(Exception exception) => SqliteErrors.TryGetUniqueViolation(exception, out _);
    }
}
