using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// Builds the tables a provider stores from the entity model, compares them with the
    /// database and brings the database in line, all in one transaction: missing databases and
    /// tables are created, a table that only lacks columns has them added in place, and a table
    /// with a column of the wrong type is rebuilt (renamed, recreated, its rows copied, the old
    /// table dropped). Column indexes and the provider's extras are synced after it commits. The
    /// provider's <see cref="ISchemaStore"/> supplies the dialect and the database access.
    /// </summary>
    public class SchemaManager(ISchemaStore store, IObjectSchemaProvider objectSchemaProvider, ILogger? logger) : IDataSchema
    {
        private bool Initialized => Status != EDataSchemaStatus.NotInitialized;
        private readonly ISchemaStore _store = store;
        private readonly IObjectSchemaProvider _objectSchemaProvider = objectSchemaProvider;
        private readonly ILogger? _logger = logger;

        public ConcurrentDictionary<Type, TableSchema> TableCache { get; } = [];

        public EDataSchemaStatus Status { get; private set; } = EDataSchemaStatus.NotInitialized;

        private ISchemaDialect Dialect => _store.Dialect;

        public TableSchema? GetTableSchema<T>() => GetTableSchema(typeof(T));
        public TableSchema? GetTableSchema(Type objectType)
        {
            if (Status == EDataSchemaStatus.Ok)
                return TableCache.TryGetValue(objectType, out var value) ? value : null;
            if (Status == EDataSchemaStatus.NotInitialized)
                if (Initialize() == EDataSchemaStatus.Ok)
                    return TableCache.TryGetValue(objectType, out var value) ? value : null;

            throw new Exception("Cannot get the table schema. Schema error.");
        }

        public EDataSchemaStatus Initialize() => InitializeCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<EDataSchemaStatus> InitializeAsync(CancellationToken? cancellationToken = null) =>
            InitializeCore(async: true, cancellationToken ?? CancellationToken.None).AsTask();

        private async ValueTask<EDataSchemaStatus> InitializeCore(bool async, CancellationToken cancellationToken)
        {
            if (Status != EDataSchemaStatus.NotInitialized)
                return Status;
            BuildSchema();
            if (!await _store.Catalog.DatabaseExistsAsync(async, cancellationToken))
            {
                if (_store.ReadOnlySchema)
                {
                    _logger?.LogError("Database does not exist and the schema is read-only. Cannot initialize.");
                    Status = EDataSchemaStatus.DatasourceNotFound;
                    return Status;
                }
                _logger?.LogInformation("Database does not exist. Creating database...");
                await _store.Catalog.CreateDatabaseAsync(async, cancellationToken);
            }
            Status = await CheckCore(async, cancellationToken);
            if (Status == EDataSchemaStatus.NotCurrent && !_store.ReadOnlySchema)
                Status = await UpdateCore(async, cancellationToken);
            return Status;
        }

        /// <summary>A table per database-enabled entity type under the provider's root.</summary>
        public List<TableSchema> BuildSchema()
        {
            // A rooted provider only builds/migrates tables for entity types under its root.
            foreach (var objSchema in _objectSchemaProvider.GetAll()!
                .Where(x => x.IsDatabaseEnabled && DataProviderRegistry.IsUnderRoot(x.Type, _store.RootType)))
            {
                if (!TableCache.ContainsKey(objSchema.Type))
                    TableCache[objSchema.Type] = new TableSchema(objSchema);
            }
            return [.. TableCache.Values];
        }

        public IEnumerable<TableSchema> GetTables() => Initialized ? TableCache.Values : BuildSchema();

        /// <summary>Compares the expected schema to the database without modifying anything.</summary>
        public EDataSchemaStatus CheckSchema() => CheckCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<EDataSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken = default) =>
            CheckCore(async: true, cancellationToken).AsTask();

        private async ValueTask<EDataSchemaStatus> CheckCore(bool async, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await _store.Catalog.ReadAsync(async, cancellationToken);
                var tables = GetTables().ToList();
                if (!SchemaComparer.CompareTables(tables, snapshot, Dialect, _logger))
                {
                    _logger?.LogWarning("Database schema is not current.");
                    return EDataSchemaStatus.NotCurrent;
                }

                var indexesMatch = true;
                foreach (var table in tables)
                {
                    var (drop, create) = SchemaComparer.DiffColumnIndexes(table, snapshot.Indexes, Dialect);
                    if (drop.Count > 0 || create.Count > 0)
                    {
                        _logger?.LogWarning("Column index schema is not current for table: {tablename}. Missing or changed: {missing}. Unexpected: {unexpected}",
                            table.TableName, string.Join(", ", create.Select(c => SchemaComparer.PhysicalIndexName(table, c, Dialect))), string.Join(", ", drop));
                        indexesMatch = false;
                    }
                    var (dropTable, createTable) = SchemaComparer.DiffTableIndexes(table, snapshot.Indexes, Dialect);
                    if (dropTable.Count > 0 || createTable.Count > 0)
                    {
                        _logger?.LogWarning("Table index schema is not current for table: {tablename}. Missing or changed: {missing}. Unexpected: {unexpected}",
                            table.TableName, string.Join(", ", createTable.Select(i => SchemaComparer.PhysicalIndexName(table, i, Dialect))), string.Join(", ", dropTable));
                        indexesMatch = false;
                    }
                }
                if (Dialect.Extras is { } extras && !await extras.AreCurrentAsync(tables, async, cancellationToken))
                    indexesMatch = false;
                return indexesMatch ? EDataSchemaStatus.Ok : EDataSchemaStatus.NotCurrent;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking the data schema. {error}", ex.Message);
                return EDataSchemaStatus.Error;
            }
        }

        public EDataSchemaStatus Update() => UpdateCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<EDataSchemaStatus> UpdateAsync(CancellationToken cancellationToken = default) =>
            UpdateCore(async: true, cancellationToken).AsTask();

        // Only call after checking the schema is not current.
        private async ValueTask<EDataSchemaStatus> UpdateCore(bool async, CancellationToken cancellationToken)
        {
            ISchemaTransaction transaction;
            try
            {
                transaction = await _store.BeginRebuildAsync(async, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating the data schema");
                return EDataSchemaStatus.Error;
            }
            var disposed = false;
            try
            {
                var tables = GetTables().ToList();
                foreach (var table in tables.Where(x => !x.IsCurrent))
                {
                    try
                    {
                        if (!await TryAddColumns(transaction, table, async, cancellationToken))
                            await RebuildTable(transaction, table, async, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger?.LogError(ex, "Error updating schema for table: {tablename}", table.TableName);
                        throw new Exception($"Error updating schema for table: {table.TableName}", ex);
                    }
                }
                // Disposing commits the rebuilds; indexes and extras are synced outside the transaction.
                disposed = true;
                await Dispose(transaction, async);
                await SyncColumnIndexes(tables, async, cancellationToken);
                if (Dialect.Extras is { } extras)
                    await extras.SyncAsync(tables, async, cancellationToken);
                return EDataSchemaStatus.Ok;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger?.LogError(ex, "Error updating the data schema");
                return EDataSchemaStatus.Error;
            }
            finally
            {
                // A failed rebuild was rolled back above; disposing ends the transaction.
                if (!disposed)
                    await Dispose(transaction, async);
            }
        }

        private static async ValueTask Dispose(ISchemaTransaction transaction, bool async)
        {
            if (async)
                await transaction.DisposeAsync();
            else
                transaction.Dispose();
        }

        /// <summary>
        /// Adds the columns an existing table lacks, when that is all it needs and the dialect can
        /// add them in place. False means the table must be created or rebuilt instead.
        /// </summary>
        private async ValueTask<bool> TryAddColumns(ISchemaTransaction transaction, TableSchema table, bool async, CancellationToken cancellationToken)
        {
            var plan = SchemaComparer.PlanUpdate(table);
            if (plan.Kind != TableUpdateKind.AddColumns)
                return false;
            var statements = Dialect.AddColumns(table, plan.Columns, plan.SoftDeleteColumns);
            if (statements == null)
                return false;
            _logger?.LogInformation("Adding columns {columns} to the Database Table of the '{objectName}' object.",
                string.Join(", ", plan.Columns.Select(c => c.ColumnName).Concat(plan.SoftDeleteColumns)), table.ObjectSchema.FullName);
            foreach (var statement in statements)
                await transaction.ExecuteAsync(statement, async, cancellationToken);
            // A legacy table without Timestamp gets it stamped, as a rebuild's copy would.
            if (plan.Columns.Any(c => Dialect.IdentifierComparer.Equals(c.ColumnName, TableSchema.TimestampColumn)))
                await transaction.ExecuteAsync(Dialect.BackfillTimestamp(table), async, cancellationToken);
            return true;
        }

        private async ValueTask RebuildTable(ISchemaTransaction transaction, TableSchema table, bool async, CancellationToken cancellationToken)
        {
            var tableName = Dialect.PhysicalName(table.TableName);
            var tempTableName = Dialect.PhysicalName($"{table.TableName}_{DateTime.Now:yyyyMMddHHmmss}");
            if (table.ExistsInDatabase)
            {
                _logger?.LogInformation("Updating the Database Table schema for the '{objectName}' object to the latest version.", table.ObjectSchema.FullName);
                foreach (var statement in Dialect.RenameForRebuild(table, tempTableName))
                    await transaction.ExecuteAsync(statement, async, cancellationToken);
            }
            else
                _logger?.LogInformation("Creating the Database Table schema for the '{objectFullname}' object.", table.ObjectSchema.FullName);

            foreach (var statement in Dialect.CreateTable(table))
                await transaction.ExecuteAsync(statement, async, cancellationToken);

            if (table.ExistsInDatabase)
            {
                var sourceColumns = await transaction.ReadColumnsAsync(tempTableName, async, cancellationToken);
                var destColumns = await transaction.ReadColumnsAsync(tableName, async, cancellationToken);
                await transaction.ExecuteAsync(
                    Dialect.CopyRows(tempTableName, tableName, CommonColumns(sourceColumns, destColumns), HasClusterId(sourceColumns)),
                    async, cancellationToken);
                await transaction.ExecuteAsync(Dialect.DropTable(tempTableName), async, cancellationToken);
                await transaction.ExecuteAsync(Dialect.BackfillTimestamp(table), async, cancellationToken);
            }
        }

        /// <summary>The columns both tables share, minus the cluster ID, which the new table generates.</summary>
        private List<string> CommonColumns(IReadOnlyList<CatalogColumn> sourceColumns, IReadOnlyList<CatalogColumn> destColumns)
        {
            var comparer = Dialect.IdentifierComparer;
            List<string> common = [];
            foreach (var name in destColumns.Select(x => x.Name))
                if (sourceColumns.Any(x => comparer.Equals(x.Name, name) && !comparer.Equals(x.Name, TableSchema.ClusterIdColumn)))
                    common.Add(name!);
            return common;
        }

        private bool HasClusterId(IReadOnlyList<CatalogColumn> columns) =>
            columns.Any(x => Dialect.IdentifierComparer.Equals(x.Name, TableSchema.ClusterIdColumn));

        /// <summary>
        /// Brings the column indexes of every table in line with the entity model: drops
        /// Nucleus-named indexes that are no longer required or whose definition changed, then
        /// creates the missing ones. Runs after the table rebuilds, so a table that was just
        /// recreated already matches and is left alone.
        /// </summary>
        private async ValueTask SyncColumnIndexes(IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var dbIndexes = await _store.Catalog.ReadIndexesAsync(async, cancellationToken);
            foreach (var table in tables)
            {
                var (drop, create) = SchemaComparer.DiffColumnIndexes(table, dbIndexes, Dialect);
                foreach (var indexName in drop)
                {
                    _logger?.LogInformation("Dropping column index {index} on table {tablename}.", indexName, table.TableName);
                    await _store.Catalog.ExecuteAsync(Dialect.DropIndex(table, indexName), async, cancellationToken);
                }
                foreach (var col in create)
                {
                    _logger?.LogInformation("Creating column index {index} on table {tablename}.", SchemaComparer.PhysicalIndexName(table, col, Dialect), table.TableName);
                    try
                    {
                        await _store.Catalog.ExecuteAsync(Dialect.CreateIndex(table, col), async, cancellationToken);
                    }
                    catch (Exception ex) when (Dialect.IsDuplicateKeyOnIndexBuild(ex))
                    {
                        throw DuplicateKeyFailure(table, col, ex);
                    }
                }
                var (dropTable, createTable) = SchemaComparer.DiffTableIndexes(table, dbIndexes, Dialect);
                foreach (var indexName in dropTable)
                {
                    _logger?.LogInformation("Dropping table index {index} on table {tablename}.", indexName, table.TableName);
                    await _store.Catalog.ExecuteAsync(Dialect.DropIndex(table, indexName), async, cancellationToken);
                }
                foreach (var index in createTable)
                {
                    // Building an index over a large table takes a while and blocks writes to it
                    // meanwhile, so the time it took is logged: a slow start can then be explained.
                    var name = SchemaComparer.PhysicalIndexName(table, index, Dialect);
                    _logger?.LogInformation("Creating table index {index} on table {tablename}.", name, table.TableName);
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    await _store.Catalog.ExecuteAsync(Dialect.CreateTableIndex(table, index), async, cancellationToken);
                    _logger?.LogInformation("Created table index {index} on table {tablename} in {seconds:0.0} s.", name, table.TableName,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
                }
            }
        }

        // Existing rows violate a newly required unique index. The data is never touched; the
        // caller must resolve the duplicates by hand and initialise again.
        private InvalidOperationException DuplicateKeyFailure(TableSchema table, ColumnSchema col, Exception inner)
        {
            var message = $"Cannot create unique index on [{table.TableName}].[{col.ColumnName}]: the column already contains duplicate values. " +
                          "Remove or change the duplicate rows and initialise the schema again. No data was modified.";
            _logger?.LogError(inner, "{message}", message);
            return new InvalidOperationException(message, inner);
        }

        #region IDataSchema
        IEnumerable<IDataTableSchema> IDataSchema.GetTables() => GetTables();
        IDataTableSchema? IDataSchema.GetTableSchema<T>() => GetTableSchema<T>();
        IDataTableSchema? IDataSchema.GetTableSchema(Type objectType) => GetTableSchema(objectType);
        public IEnumerable<IDataTableSchema> GetTableSchemas() => GetTables();
        public bool IsCompatible(IEnumerable<IObjectSchema> objSchemas) =>
            objSchemas.All(schema => GetTables().Any(table => table.ObjectSchema.Type == schema.Type && table.IsCurrent));
        #endregion
    }
}
