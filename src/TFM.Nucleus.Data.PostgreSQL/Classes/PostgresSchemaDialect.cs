using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using static TFM.Nucleus.Data.PostgreSQL.PostgresIdentifiers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// Every DDL string the PostgreSQL provider runs. A table has the Guid primary key every
    /// entity carries, an identity ClusterID that keeps a rebuild's copy in insertion order, a
    /// column per [DatabaseColumn] property, the soft-delete columns where the type needs them,
    /// and an index per Indexed or Unique column. Names longer than PostgreSQL's 63 bytes are
    /// shortened the same way everywhere they are written and compared.
    /// </summary>
    internal sealed class PostgresSchemaDialect(string schema, ISchemaExtras? extras) : ISchemaDialect
    {
        internal const string FullTextIndexPrefix = "FTS_";
        internal const string NotifyTriggerPrefix = "NTF_";
        internal const string NotifyFunction = "nucleus_notify";
        private const string NotifyChannelPrefix = "nucleus_";

        public string Schema { get; } = schema;

        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public string PhysicalName(string logicalName) => Physical(logicalName);

        public bool ColumnMatches(ColumnSchema column, CatalogColumn existing) => PostgresTypes.Matches(column, existing);

        public ISchemaExtras? Extras { get; } = extras;

        private string TableName(string tableName) => Table(Schema, tableName);

        public IReadOnlyList<SchemaStatement> CreateTable(TableSchema table)
        {
            var idColumn = table.IdentityColumn.ColumnName;
            var sb = new StringBuilder($"CREATE TABLE {TableName(table.TableName)} (");
            sb.Append($"{Quote(idColumn)} uuid NOT NULL DEFAULT gen_random_uuid(), ");
            sb.Append($"{Quote(TableSchema.ClusterIdColumn)} bigint GENERATED ALWAYS AS IDENTITY, ");
            foreach (var col in table.Columns)
                if (col.ColumnName != idColumn)
                    sb.Append($"{ColumnDefinition(col)}, ");
            if (table.IsSoftDeletable)
                sb.Append($"{SoftDeleteColumnDefinition(SoftDelete.IsDeletedColumn)}, {SoftDeleteColumnDefinition(SoftDelete.DeletedAtColumn)}, ");
            sb.Append($"CONSTRAINT {Quote($"PK_{table.TableName}")} PRIMARY KEY ({Quote(idColumn)}))");

            List<SchemaStatement> statements = [$"CREATE SCHEMA IF NOT EXISTS {Quote(Schema)}", sb.ToString()];
            foreach (var col in table.IndexedColumns)
                statements.Add(CreateIndex(table, col));
            return statements;
        }

        /// <summary>An entity column, as CREATE TABLE and ALTER TABLE ADD COLUMN both write it. Every column is created NULL.</summary>
        private static string ColumnDefinition(ColumnSchema column) => $"{Quote(column.ColumnName)} {PostgresTypes.TypeName(column)} NULL";

        /// <summary>One soft-delete system column, as CREATE TABLE and ALTER TABLE ADD COLUMN both write it.</summary>
        private static string SoftDeleteColumnDefinition(string column) => column switch
        {
            SoftDelete.IsDeletedColumn => $"{Quote(SoftDelete.IsDeletedColumn)} boolean NOT NULL DEFAULT false",
            SoftDelete.DeletedAtColumn => $"{Quote(SoftDelete.DeletedAtColumn)} timestamptz NULL",
            _ => throw new ArgumentException($"'{column}' is not a soft-delete system column.", nameof(column)),
        };

        /// <summary>
        /// Adds columns to an existing table in one statement. A NULL column, and a NOT NULL one
        /// with a constant default such as IsDeleted, is a catalog-only change at any table size.
        /// </summary>
        public IReadOnlyList<SchemaStatement>? AddColumns(TableSchema table, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> softDeleteColumns)
        {
            if (columns.Count == 0 && softDeleteColumns.Count == 0)
                throw new ArgumentException("There are no columns to add.", nameof(columns));
            var definitions = columns.Select(ColumnDefinition).Concat(softDeleteColumns.Select(SoftDeleteColumnDefinition));
            return [$"ALTER TABLE {TableName(table.TableName)} {string.Join(", ", definitions.Select(d => $"ADD COLUMN {d}"))}"];
        }

        /// <summary>
        /// Renames the table, then gives its indexes (the primary key's included, which renames
        /// the constraint) and its identity sequence throwaway names: in PostgreSQL those names are
        /// unique across the schema, so the new table could not take them otherwise.
        /// </summary>
        public IReadOnlyList<SchemaStatement> RenameForRebuild(TableSchema table, string tempTableName)
        {
            var schemaLiteral = Literal(Schema);
            var tempLiteral = Literal(tempTableName);
            return
            [
                $"ALTER TABLE {TableName(table.TableName)} RENAME TO {Quote(tempTableName)}",
                $@"DO $nucleus$
DECLARE
    r record;
    seq text;
BEGIN
    FOR r IN SELECT i.relname FROM pg_index x
        JOIN pg_class i ON i.oid = x.indexrelid
        JOIN pg_class t ON t.oid = x.indrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = {schemaLiteral} AND t.relname = {tempLiteral}
    LOOP
        EXECUTE format('ALTER INDEX %I.%I RENAME TO %I', {schemaLiteral}, r.relname, 'rb_' || left(md5({tempLiteral} || r.relname), 24));
    END LOOP;
    seq := pg_get_serial_sequence(format('%I.%I', {schemaLiteral}, {tempLiteral}), {Literal(TableSchema.ClusterIdColumn)});
    IF seq IS NOT NULL THEN
        EXECUTE format('ALTER SEQUENCE %s RENAME TO %I', seq, 'rb_' || left(md5({tempLiteral} || seq), 24));
    END IF;
END
$nucleus$",
            ];
        }

        public SchemaStatement CopyRows(string sourceTableName, string destTableName, IReadOnlyList<string> columns, bool orderByClusterId)
        {
            var list = string.Join(", ", columns.Select(Quote));
            var qry = $"INSERT INTO {TableName(destTableName)} ({list}) SELECT {list} FROM {TableName(sourceTableName)}";
            if (orderByClusterId)
                qry += $" ORDER BY {Quote(TableSchema.ClusterIdColumn)}";
            return qry;
        }

        public SchemaStatement DropTable(string tableName) => $"DROP TABLE {TableName(tableName)}";

        public SchemaStatement BackfillTimestamp(TableSchema table) =>
            $"UPDATE {TableName(table.TableName)} SET {Quote(TableSchema.TimestampColumn)} = clock_timestamp() WHERE {Quote(TableSchema.TimestampColumn)} IS NULL";

        /// <summary>
        /// A plain or unique index on one column. A unique index is filtered to non-null keys,
        /// as on SQL Server, so the schema check reads the same shape back on both engines.
        /// </summary>
        public SchemaStatement CreateIndex(TableSchema table, ColumnSchema column)
        {
            if (!column.HasColumnIndex)
                throw new ArgumentException($"Column '{column.ColumnName}' does not carry a column index.", nameof(column));
            var name = Quote(IndexNaming.ColumnIndexName(table, column));
            var key = $"({Quote(column.ColumnName)} {(column.IndexSortDescending ? "DESC" : "ASC")})";
            return column.Unique
                ? $"CREATE UNIQUE INDEX {name} ON {TableName(table.TableName)} {key} WHERE {Quote(column.ColumnName)} IS NOT NULL"
                : $"CREATE INDEX {name} ON {TableName(table.TableName)} {key}";
        }

        public SchemaStatement DropIndex(TableSchema table, string physicalIndexName) =>
            $"DROP INDEX {Quote(Schema)}.{Quote(physicalIndexName)}";

        /// <summary>A [TableIndex] index: its keys ascending, then its included columns (PostgreSQL 11 and later).</summary>
        public SchemaStatement CreateTableIndex(TableSchema table, TableIndexSchema index)
        {
            var keys = string.Join(", ", index.Keys.Select(k => $"{Quote(k.ColumnName)} ASC"));
            var include = index.Include.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", index.Include.Select(c => Quote(c.ColumnName)))})";
            return $"CREATE INDEX {Quote(IndexNaming.TableIndexName(table, index))} ON {TableName(table.TableName)} ({keys}){include}";
        }

        public bool IsDuplicateKeyOnIndexBuild(Exception exception) => PostgresErrors.TryGetUniqueViolation(exception, out _);

        // ---- Full-text search and change notification objects.

        internal static string FullTextIndexName(TableSchema table, ColumnSchema column) => Physical($"{FullTextIndexPrefix}{table.TableName}_{column.ColumnName}");

        internal static string NotifyTriggerName(TableSchema table) => Physical($"{NotifyTriggerPrefix}{table.TableName}");

        /// <summary>The LISTEN channel a table's trigger notifies on every write.</summary>
        internal static string NotifyChannel(string tableName) => Physical($"{NotifyChannelPrefix}{tableName}");

        /// <summary>
        /// A GIN index over the column's tsvector. Its expression is exactly the one the renderer
        /// writes for string.Contains on a FullTextSearch column, so the search can use it.
        /// </summary>
        internal string CreateFullTextIndex(TableSchema table, ColumnSchema column) =>
            $"CREATE INDEX {Quote(FullTextIndexName(table, column))} ON {TableName(table.TableName)} USING gin (to_tsvector('english', {Quote(column.ColumnName)}))";

        internal string DropFullTextIndex(string physicalIndexName) => $"DROP INDEX {Quote(Schema)}.{Quote(physicalIndexName)}";

        /// <summary>The trigger function every notification trigger runs: it notifies the channel it was created with.</summary>
        internal string CreateNotifyFunction() =>
            $@"CREATE OR REPLACE FUNCTION {Quote(Schema)}.{Quote(NotifyFunction)}() RETURNS trigger LANGUAGE plpgsql AS $nucleus$
BEGIN
    PERFORM pg_notify(TG_ARGV[0], TG_OP);
    RETURN NULL;
END
$nucleus$";

        /// <summary>One notification per statement that writes the table, whatever it changed.</summary>
        internal string CreateNotifyTrigger(TableSchema table) =>
            $"CREATE TRIGGER {Quote(NotifyTriggerName(table))} AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON {TableName(table.TableName)} " +
            $"FOR EACH STATEMENT EXECUTE FUNCTION {Quote(Schema)}.{Quote(NotifyFunction)}({Literal(NotifyChannel(table.TableName))})";
    }

    /// <summary>
    /// The PostgreSQL objects beyond tables and column indexes: a GIN index per FullTextSearch
    /// column, and a statement-level trigger per table that notifies the table's channel, which
    /// change subscriptions LISTEN on.
    /// </summary>
    internal sealed class PostgresSchemaExtras(PostgresCatalogReader catalog, Func<PostgresSchemaDialect> dialect, ILogger? logger) : ISchemaExtras
    {
        public async ValueTask<bool> AreCurrentAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var (missingFullText, staleFullText) = await DiffFullText(tables, async, cancellationToken);
            var missingTriggers = await MissingTriggers(tables, async, cancellationToken);
            var functionExists = await catalog.NotifyFunctionExistsAsync(async, cancellationToken);
            foreach (var (table, column) in missingFullText)
                logger?.LogWarning("Full-text index is missing for column {column} of table {tablename}", column.ColumnName, table.TableName);
            foreach (var (table, index) in staleFullText)
                logger?.LogWarning("Full-text index {index} on table {tablename} is no longer needed", index, table);
            foreach (var table in missingTriggers)
                logger?.LogWarning("Change notification trigger is missing for table {tablename}", table.TableName);
            if (!functionExists)
                logger?.LogWarning("The change notification function is missing.");
            return missingFullText.Count == 0 && staleFullText.Count == 0 && missingTriggers.Count == 0 && functionExists;
        }

        public async ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var d = dialect();
            var (missingFullText, staleFullText) = await DiffFullText(tables, async, cancellationToken);
            foreach (var (_, index) in staleFullText)
                await catalog.ExecuteAsync(d.DropFullTextIndex(index), async, cancellationToken);
            foreach (var (table, column) in missingFullText)
                await catalog.ExecuteAsync(d.CreateFullTextIndex(table, column), async, cancellationToken);

            await catalog.ExecuteAsync(d.CreateNotifyFunction(), async, cancellationToken);
            foreach (var table in await MissingTriggers(tables, async, cancellationToken))
                await catalog.ExecuteAsync(d.CreateNotifyTrigger(table), async, cancellationToken);
        }

        private async ValueTask<(List<(TableSchema Table, ColumnSchema Column)> Missing, List<(string Table, string Index)> Stale)> DiffFullText(
            IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var existing = await catalog.ReadFullTextIndexesAsync(async, cancellationToken);
            var required = tables
                .SelectMany(t => t.Columns.Where(c => c.FullTextSearch).Select(c => (Table: t, Column: c)))
                .ToList();
            var requiredNames = required.Select(r => PostgresSchemaDialect.FullTextIndexName(r.Table, r.Column)).ToHashSet(StringComparer.Ordinal);
            var ownTables = tables.Select(t => Physical(t.TableName)).ToHashSet(StringComparer.Ordinal);
            var existingNames = existing.Select(e => e.Index).ToHashSet(StringComparer.Ordinal);
            var missing = required.Where(r => !existingNames.Contains(PostgresSchemaDialect.FullTextIndexName(r.Table, r.Column))).ToList();
            var stale = existing.Where(e => ownTables.Contains(e.Table) && !requiredNames.Contains(e.Index)).ToList();
            return (missing, stale);
        }

        private async ValueTask<List<TableSchema>> MissingTriggers(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var existing = (await catalog.ReadNotifyTriggersAsync(async, cancellationToken))
                .Select(t => (t.Table, t.Trigger)).ToHashSet();
            return tables.Where(t => !existing.Contains((Physical(t.TableName), PostgresSchemaDialect.NotifyTriggerName(t)))).ToList();
        }
    }
}
