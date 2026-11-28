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
        internal const string ClusterIdIndexPrefix = "CIX_";
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

            List<SchemaStatement> statements = [$"CREATE SCHEMA IF NOT EXISTS {Quote(Schema)}", sb.ToString(), CreateClusterIdIndex(table.TableName)];
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

        /// <summary>
        /// Renames the legacy table, drops its change notification trigger (it notifies the old
        /// table's channel; the extras sync creates the new one), and renames the primary key
        /// (which renames its constraint) and every index the entity would have under the old name
        /// to the name it has under the new one. Each rename is IF EXISTS, so an index the old
        /// table never had is skipped and created by the index sync, and an index the entity no
        /// longer declares keeps its old name until the sync drops it. The identity sequence keeps
        /// its name: PostgreSQL picks a free one for any later table.
        /// </summary>
        public IReadOnlyList<SchemaStatement> AdoptTable(string legacyTableName, TableSchema table)
        {
            var newName = table.TableName;
            List<SchemaStatement> statements =
            [
                $"ALTER TABLE {TableName(legacyTableName)} RENAME TO {Quote(newName)}",
                $"DROP TRIGGER IF EXISTS {Quote(NotifyTriggerName(legacyTableName))} ON {TableName(newName)}",
            ];
            void Rename(string oldIndex, string newIndex)
            {
                if (Physical(oldIndex) != Physical(newIndex))
                    statements.Add($"ALTER INDEX IF EXISTS {Quote(Schema)}.{Quote(oldIndex)} RENAME TO {Quote(newIndex)}");
            }
            Rename($"PK_{legacyTableName}", $"PK_{newName}");
            foreach (var column in table.IndexedColumns)
                Rename(IndexNaming.ColumnIndexName(legacyTableName, column), IndexNaming.ColumnIndexName(newName, column));
            foreach (var index in table.TableIndexes)
                Rename(IndexNaming.TableIndexName(legacyTableName, index), IndexNaming.TableIndexName(newName, index));
            foreach (var column in table.Columns.Where(c => c.FullTextSearch))
                Rename(FullTextIndexName(legacyTableName, column), FullTextIndexName(newName, column));
            Rename(ClusterIdIndexName(legacyTableName), ClusterIdIndexName(newName));
            return statements;
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
            var key = $"({Quote(column.ColumnName)} {(column.IndexDescending ? DescendingKey : AscendingKey)})";
            return column.Unique
                ? $"CREATE UNIQUE INDEX {name} ON {TableName(table.TableName)} {key} WHERE {Quote(column.ColumnName)} IS NOT NULL"
                : $"CREATE INDEX {name} ON {TableName(table.TableName)} {key}";
        }

        // An index key places a null below every value, as every ordered read does
        // (PostgresQueryRenderer.OrderTerm). PostgreSQL's own default places it above, and an
        // index made that way matches an order read neither forwards nor backwards, so no ordered
        // read could use one: measured on 100,000 rows, a first page of 50 took 21 ms and read
        // every page of the table, against 0.07 ms and 55 pages with the nulls placed low.
        private const string AscendingKey = "ASC NULLS FIRST";
        private const string DescendingKey = "DESC NULLS LAST";

        public SchemaStatement DropIndex(TableSchema table, string physicalIndexName) =>
            $"DROP INDEX {Quote(Schema)}.{Quote(physicalIndexName)}";

        /// <summary>
        /// A [TableIndex] index: its keys ascending, then its included columns (PostgreSQL 11 and
        /// later). A unique one is filtered to rows whose keys are all non-null, as on SQL Server.
        /// </summary>
        public SchemaStatement CreateTableIndex(TableSchema table, TableIndexSchema index)
        {
            var keys = string.Join(", ", index.Keys.Select(k => $"{Quote(k.ColumnName)} {AscendingKey}"));
            var include = index.Include.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", index.Include.Select(c => Quote(c.ColumnName)))})";
            var target = $"{Quote(IndexNaming.TableIndexName(table, index))} ON {TableName(table.TableName)} ({keys}){include}";
            return index.Unique
                ? $"CREATE UNIQUE INDEX {target} WHERE {string.Join(" AND ", index.Keys.Select(k => $"{Quote(k.ColumnName)} IS NOT NULL"))}"
                : $"CREATE INDEX {target}";
        }

        public bool IsDuplicateKeyOnIndexBuild(Exception exception) => PostgresErrors.TryGetUniqueViolation(exception, out _);

        public bool SupportsForeignKeys => true;

        /// <summary>Every table carries ClusterID, which an unordered page is ordered by.</summary>
        public bool HasClusterIdColumn => true;

        /// <summary>
        /// The key as a named constraint. NOT VALID leaves the rows that were there unchecked;
        /// every row written after it is checked.
        /// </summary>
        public SchemaStatement AddForeignKey(ForeignKeySchema key, bool checkExisting) =>
            $"ALTER TABLE {TableName(key.Table.TableName)} ADD CONSTRAINT {Quote(key.Name)} FOREIGN KEY ({Quote(key.Column.ColumnName)}) " +
            $"REFERENCES {TableName(key.Referenced.TableName)} ({Quote(key.Referenced.IdentityColumn!.ColumnName)})" +
            (key.OnDelete == ForeignKeyAction.SetNull ? " ON DELETE SET NULL" : "") +
            (checkExisting ? "" : " NOT VALID");

        public SchemaStatement DropForeignKey(string physicalTableName, string physicalKeyName) =>
            $"ALTER TABLE {TableName(physicalTableName)} DROP CONSTRAINT {Quote(physicalKeyName)}";

        public SchemaStatement CountOrphans(ForeignKeySchema key) =>
            $"SELECT count(*) FROM {TableName(key.Table.TableName)} c WHERE c.{Quote(key.Column.ColumnName)} IS NOT NULL " +
            $"AND NOT EXISTS (SELECT 1 FROM {TableName(key.Referenced.TableName)} r WHERE r.{Quote(key.Referenced.IdentityColumn!.ColumnName)} = c.{Quote(key.Column.ColumnName)})";

        // ---- The ClusterID index, full-text search and change notification objects.

        internal static string ClusterIdIndexName(string tableName) => Physical($"{ClusterIdIndexPrefix}{tableName}_{TableSchema.ClusterIdColumn}");

        /// <summary>
        /// The unique index on ClusterID, which a page with no order of its own is ordered by:
        /// without it each page would sort every matching row. SQL Server's ClusterID is its
        /// clustered index; PostgreSQL's must be created.
        /// </summary>
        internal string CreateClusterIdIndex(string tableName) =>
            $"CREATE UNIQUE INDEX {Quote(ClusterIdIndexName(tableName))} ON {TableName(tableName)} ({Quote(TableSchema.ClusterIdColumn)})";

        internal string DropClusterIdIndex(string physicalIndexName) => $"DROP INDEX {Quote(Schema)}.{Quote(physicalIndexName)}";

        internal static string FullTextIndexName(TableSchema table, ColumnSchema column) => FullTextIndexName(table.TableName, column);

        internal static string FullTextIndexName(string tableName, ColumnSchema column) => Physical($"{FullTextIndexPrefix}{tableName}_{column.ColumnName}");

        internal static string NotifyTriggerName(TableSchema table) => NotifyTriggerName(table.TableName);

        internal static string NotifyTriggerName(string tableName) => Physical($"{NotifyTriggerPrefix}{tableName}");

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
            var (missingClusterId, staleClusterId) = await DiffClusterIdIndexes(tables, async, cancellationToken);
            foreach (var table in missingClusterId)
                logger?.LogWarning("The {column} index is missing for table {tablename}", TableSchema.ClusterIdColumn, table.TableName);
            foreach (var (table, index) in staleClusterId)
                logger?.LogWarning("{column} index {index} on table {tablename} has another table's name", TableSchema.ClusterIdColumn, index, table);
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
            return missingClusterId.Count == 0 && staleClusterId.Count == 0 &&
                missingFullText.Count == 0 && staleFullText.Count == 0 && missingTriggers.Count == 0 && functionExists;
        }

        public async ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var d = dialect();
            // A table created before ClusterID was indexed gains the index in place; building it
            // reads the table once, which is logged so a slow start can be explained.
            var (missingClusterId, staleClusterId) = await DiffClusterIdIndexes(tables, async, cancellationToken);
            foreach (var (_, index) in staleClusterId)
                await catalog.ExecuteAsync(d.DropClusterIdIndex(index), async, cancellationToken);
            foreach (var table in missingClusterId)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await catalog.ExecuteAsync(d.CreateClusterIdIndex(table.TableName), async, cancellationToken);
                logger?.LogInformation("Created the {column} index on table {tablename} in {seconds:0.0} s.", TableSchema.ClusterIdColumn, table.TableName,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
            }

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

        /// <summary>
        /// The tables whose ClusterID index is missing, and the ClusterID indexes on this model's
        /// tables under a name that is not the table's own (one kept from a table name the
        /// table no longer has).
        /// </summary>
        private async ValueTask<(List<TableSchema> Missing, List<(string Table, string Index)> Stale)> DiffClusterIdIndexes(
            IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var existing = await catalog.ReadClusterIdIndexesAsync(async, cancellationToken);
            var existingNames = existing.Select(e => (e.Table, e.Index)).ToHashSet();
            var missing = tables.Where(t => !existingNames.Contains((Physical(t.TableName), PostgresSchemaDialect.ClusterIdIndexName(t.TableName)))).ToList();
            var ownTables = tables.ToDictionary(t => Physical(t.TableName), t => PostgresSchemaDialect.ClusterIdIndexName(t.TableName), StringComparer.Ordinal);
            var stale = existing.Where(e => ownTables.TryGetValue(e.Table, out var name) && e.Index != name).ToList();
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
