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
        private bool Initialized => Status != DataSchemaStatus.NotInitialized;
        private readonly ISchemaStore _store = store;
        private readonly IObjectSchemaProvider _objectSchemaProvider = objectSchemaProvider;
        private readonly ILogger? _logger = logger;

        public ConcurrentDictionary<Type, TableSchema> TableCache { get; } = [];

        public DataSchemaStatus Status { get; private set; } = DataSchemaStatus.NotInitialized;

        /// <summary>
        /// Which of this provider's types' rows point at which others', the order a save writes
        /// and a delete removes several types in. Built with the tables.
        /// </summary>
        internal ReferenceGraph References { get; private set; } = ReferenceGraph.Empty;

        private ISchemaDialect Dialect => _store.Dialect;

        public TableSchema? GetTableSchema<T>() => GetTableSchema(typeof(T));
        public TableSchema? GetTableSchema(Type objectType)
        {
            if (Status == DataSchemaStatus.Ok)
                return TableCache.TryGetValue(objectType, out var value) ? value : null;
            if (Status == DataSchemaStatus.NotInitialized)
                if (Initialize() == DataSchemaStatus.Ok)
                    return TableCache.TryGetValue(objectType, out var value) ? value : null;

            throw new Exception("Cannot get the table schema. Schema error.");
        }

        public DataSchemaStatus Initialize() => InitializeCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<DataSchemaStatus> InitializeAsync(CancellationToken? cancellationToken = null) =>
            InitializeCore(async: true, cancellationToken ?? CancellationToken.None).AsTask();

        private async ValueTask<DataSchemaStatus> InitializeCore(bool async, CancellationToken cancellationToken)
        {
            if (Status != DataSchemaStatus.NotInitialized)
                return Status;
            BuildSchema();
            if (!await _store.Catalog.DatabaseExistsAsync(async, cancellationToken))
            {
                if (_store.ReadOnlySchema)
                {
                    _logger?.LogError("Database does not exist and the schema is read-only. Cannot initialize.");
                    Status = DataSchemaStatus.DatasourceNotFound;
                    return Status;
                }
                _logger?.LogInformation("Database does not exist. Creating database...");
                await _store.Catalog.CreateDatabaseAsync(async, cancellationToken);
            }
            Status = await CheckCore(async, cancellationToken);
            if (Status == DataSchemaStatus.NotCurrent && !_store.ReadOnlySchema)
                Status = await UpdateCore(async, cancellationToken);
            return Status;
        }

        /// <summary>
        /// A table per database-enabled entity type under the provider's root, named by the
        /// provider's naming policy unless the type names its own. Two types with one table name,
        /// or a type whose old name is another type's table or another type's old name, fail here.
        /// </summary>
        public List<TableSchema> BuildSchema()
        {
            // A rooted provider only builds/migrates tables for entity types under its root.
            foreach (var objSchema in _objectSchemaProvider.GetAll()!
                .Where(x => x.IsDatabaseEnabled && DataProviderRegistry.IsUnderRoot(x.Type, _store.RootType)))
            {
                if (!TableCache.ContainsKey(objSchema.Type))
                    TableCache[objSchema.Type] = new TableSchema(objSchema, _store.TableNaming);
            }
            List<TableSchema> tables = [.. TableCache.Values];
            EnsureTableNamesAreDistinct(tables);
            EnsureLinkedCollectionsAreStoredTogether(tables);
            EnsureForceSaveLinksAreRoots(tables);
            References = new ReferenceGraph(tables.Select(t => t.ObjectSchema));
            return tables;
        }

        /// <summary>
        /// Refuses a <c>[LinkedObject(forceSave: true)]</c> typed as a child: the save would write
        /// the child as a root, outside the parent that owns it. A plain link to a child is fine;
        /// the child is saved through its parent.
        /// </summary>
        private static void EnsureForceSaveLinksAreRoots(IReadOnlyList<TableSchema> tables)
        {
            foreach (var table in tables)
            {
                var schema = table.ObjectSchema;
                if (!schema.HasLinkedObjects)
                    continue;
                foreach (var link in schema.LinkedObjectProperties!)
                    if (link.ForceLinkedSave && link.ObjectSchema is { HasParentObject: true } target)
                        throw new InvalidOperationException(
                            $"{schema.Name}.{link.Name} is a [LinkedObject(forceSave: true)] that holds a {target.Name}, which is a child: " +
                            $"it is saved through its parent's collection, never on its own. Drop forceSave and save the parent.");
            }
        }

        /// <summary>
        /// Checks every linked collection's key against its many type, and refuses a collection
        /// whose many type this provider does not store: the members are saved in the one's
        /// transaction, included in its read, and looked for before it is deleted, all on one
        /// connection.
        /// </summary>
        private void EnsureLinkedCollectionsAreStoredTogether(IReadOnlyList<TableSchema> tables)
        {
            foreach (var table in tables)
            {
                var schema = table.ObjectSchema;
                if (!schema.HasLinkedCollections)
                    continue;
                foreach (var collection in schema.LinkedCollectionProperties)
                {
                    _ = schema.GetLinkedCollectionKey(collection);
                    if (!TableCache.ContainsKey(collection.LinkedType!))
                        throw new InvalidOperationException(
                            $"{schema.Name}.{collection.Name} holds {collection.LinkedType!.Name}, which is not stored by the provider that stores {schema.Name}. " +
                            "The two types of a linked collection are stored by one provider.");
                }
            }
        }

        /// <summary>
        /// Names are compared without regard to case, so the entities are valid on every provider,
        /// and again as this engine stores them, so a shortened name cannot collide either.
        /// </summary>
        private void EnsureTableNamesAreDistinct(IReadOnlyList<TableSchema> tables)
        {
            var owners = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
            var physicalOwners = new Dictionary<string, TableSchema>(Dialect.IdentifierComparer);
            foreach (var table in tables.OrderBy(t => t.ObjectSchema.FullName, StringComparer.Ordinal))
            {
                if (owners.TryGetValue(table.TableName, out var other) ||
                    physicalOwners.TryGetValue(Dialect.PhysicalName(table.TableName), out other))
                    throw new InvalidOperationException(
                        $"Types '{other.ObjectSchema.FullName}' and '{table.ObjectSchema.FullName}' both map to table '{table.TableName}'. " +
                        "Give one of them its own name with [DatabaseTable], or a naming policy that keeps more levels.");
                owners[table.TableName] = table;
                physicalOwners[Dialect.PhysicalName(table.TableName)] = table;
            }

            var claims = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables.OrderBy(t => t.ObjectSchema.FullName, StringComparer.Ordinal))
                foreach (var name in AdoptionCandidates(table))
                {
                    if (owners.TryGetValue(name, out var owner) || physicalOwners.TryGetValue(Dialect.PhysicalName(name), out owner))
                        throw new InvalidOperationException(
                            $"'{table.ObjectSchema.FullName}' would adopt table '{name}' as an old name, but that is the table of '{owner.ObjectSchema.FullName}'. " +
                            "Renaming it would take that type's rows. Remove the name from [LegacyTableNames], or rename one of the types' tables.");
                    if (claims.TryGetValue(name, out var claimant))
                        throw new InvalidOperationException(
                            $"Types '{claimant.ObjectSchema.FullName}' and '{table.ObjectSchema.FullName}' both claim table '{name}' as an old name. " +
                            "Only one of them can adopt it.");
                    claims[name] = table;
                }
        }

        /// <summary>
        /// The old names a missing table is adopted from: its [LegacyTableNames], then its
        /// full-name form when the provider renames full-name tables and the name has changed.
        /// </summary>
        private List<string> AdoptionCandidates(TableSchema table)
        {
            List<string> names = [.. table.LegacyTableNames];
            if (_store.RenameFullNameTables &&
                !table.FullTableName.Equals(table.TableName, StringComparison.OrdinalIgnoreCase) &&
                !names.Contains(table.FullTableName, StringComparer.OrdinalIgnoreCase))
                names.Add(table.FullTableName);
            return names;
        }

        /// <summary>The old names of <paramref name="table"/> that exist as tables in <paramref name="snapshot"/>.</summary>
        private List<string> PresentAdoptionCandidates(TableSchema table, CatalogSnapshot snapshot) =>
            [.. AdoptionCandidates(table).Where(name => snapshot.Tables.Contains(Dialect.PhysicalName(name), Dialect.IdentifierComparer))];

        /// <summary>
        /// Reports what the update would do with old tables: adopt the one old table of a missing
        /// table, refuse when there are several, and leave alone an old table whose successor exists.
        /// </summary>
        private void LogAdoptions(IReadOnlyList<TableSchema> tables, CatalogSnapshot snapshot)
        {
            foreach (var table in tables)
            {
                var present = PresentAdoptionCandidates(table, snapshot);
                if (present.Count == 0)
                    continue;
                if (table.ExistsInDatabase)
                    _logger?.LogWarning("Table {tablename} exists, so its old table {legacy} is left as it is. Move or drop its rows by hand.",
                        table.TableName, string.Join(", ", present));
                else if (present.Count == 1)
                    _logger?.LogInformation("Table {tablename} is missing; the schema update renames its old table {legacy} to it.",
                        table.TableName, present[0]);
                else
                    _logger?.LogWarning("Table {tablename} is missing and several of its old tables exist ({legacy}); the schema update refuses to choose one.",
                        table.TableName, string.Join(", ", present));
            }
        }

        public IEnumerable<TableSchema> GetTables() => Initialized ? TableCache.Values : BuildSchema();

        /// <summary>Compares the expected schema to the database without modifying anything.</summary>
        public DataSchemaStatus CheckSchema() => CheckCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<DataSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken = default) =>
            CheckCore(async: true, cancellationToken).AsTask();

        private async ValueTask<DataSchemaStatus> CheckCore(bool async, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await _store.Catalog.ReadAsync(async, cancellationToken);
                var tables = GetTables().ToList();
                var tablesCurrent = SchemaComparer.CompareTables(tables, snapshot, Dialect, _logger);
                LogAdoptions(tables, snapshot);
                if (!tablesCurrent)
                {
                    _logger?.LogWarning("Database schema is not current.");
                    return DataSchemaStatus.NotCurrent;
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
                return indexesMatch ? DataSchemaStatus.Ok : DataSchemaStatus.NotCurrent;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking the data schema. {error}", ex.Message);
                return DataSchemaStatus.Error;
            }
        }

        public DataSchemaStatus Update() => UpdateCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<DataSchemaStatus> UpdateAsync(CancellationToken cancellationToken = default) =>
            UpdateCore(async: true, cancellationToken).AsTask();

        // Only call after checking the schema is not current.
        private async ValueTask<DataSchemaStatus> UpdateCore(bool async, CancellationToken cancellationToken)
        {
            ISchemaTransaction transaction;
            try
            {
                transaction = await _store.BeginRebuildAsync(async, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating the data schema");
                return DataSchemaStatus.Error;
            }
            var disposed = false;
            try
            {
                var tables = GetTables().ToList();
                await AdoptLegacyTables(transaction, tables, async, cancellationToken);
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
                // Indexes and extras are synced outside the transaction.
                await transaction.CommitAsync(async, cancellationToken);
                disposed = true;
                await Dispose(transaction, async);
                await SyncColumnIndexes(tables, async, cancellationToken);
                if (Dialect.Extras is { } extras)
                    await extras.SyncAsync(tables, async, cancellationToken);
                return DataSchemaStatus.Ok;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger?.LogError(ex, "Error updating the data schema");
                return DataSchemaStatus.Error;
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
        /// Renames to its table the one old table of each missing table, inside the schema
        /// transaction, then compares it again so the rest of the update treats it as an existing
        /// table: columns it lacks are added, a changed column rebuilds it. Several old tables of one
        /// table fail the update, which rolls everything back: their rows cannot be merged safely.
        /// </summary>
        private async ValueTask AdoptLegacyTables(ISchemaTransaction transaction, IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var missing = tables.Where(t => !t.ExistsInDatabase && AdoptionCandidates(t).Count > 0).ToList();
            if (missing.Count == 0)
                return;
            var snapshot = await _store.Catalog.ReadAsync(async, cancellationToken);
            foreach (var table in missing)
            {
                var physical = Dialect.PhysicalName(table.TableName);
                // The table may have appeared since the check, from another process starting up.
                if (snapshot.Tables.Contains(physical, Dialect.IdentifierComparer))
                    continue;
                var present = PresentAdoptionCandidates(table, snapshot);
                if (present.Count == 0)
                    continue;
                if (present.Count > 1)
                    throw new InvalidOperationException(
                        $"Table '{table.TableName}' of '{table.ObjectSchema.FullName}' is missing, and more than one of its old tables exists: " +
                        $"{string.Join(", ", present)}. Nucleus does not merge tables, because their rows can share IDs and their columns can differ. " +
                        "Move the rows you want to keep into one of them, drop the others, and start again. Nothing was changed.");

                _logger?.LogInformation("Renaming table {legacy} to {tablename} for the '{objectName}' object.", present[0], table.TableName, table.ObjectSchema.FullName);
                foreach (var statement in Dialect.AdoptTable(present[0], table))
                    await transaction.ExecuteAsync(statement, async, cancellationToken);

                var columns = await transaction.ReadColumnsAsync(physical, async, cancellationToken);
                if (columns.Count == 0)
                    throw new InvalidOperationException($"Table '{present[0]}' was renamed to '{table.TableName}', but the renamed table cannot be read back.");
                SchemaComparer.CompareTables([table], new CatalogSnapshot([physical], columns, []), Dialect, _logger);
            }
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
                        throw DuplicateKeyFailure(table, IndexNaming.ColumnIndexName(table, col), [col], ex);
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
                    try
                    {
                        await _store.Catalog.ExecuteAsync(Dialect.CreateTableIndex(table, index), async, cancellationToken);
                    }
                    catch (Exception ex) when (Dialect.IsDuplicateKeyOnIndexBuild(ex))
                    {
                        throw DuplicateKeyFailure(table, IndexNaming.TableIndexName(table, index), index.Keys, ex);
                    }
                    _logger?.LogInformation("Created table index {index} on table {tablename} in {seconds:0.0} s.", name, table.TableName,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
                }
            }
        }

        // Existing rows violate a newly required unique index, a column's or a [TableIndex]. The
        // data is never touched; the caller must resolve the duplicates by hand and initialise again.
        private InvalidOperationException DuplicateKeyFailure(TableSchema table, string indexName, IReadOnlyList<ColumnSchema> keys, Exception inner)
        {
            var columns = string.Join(", ", keys.Select(k => $"[{k.ColumnName}]"));
            var already = keys.Count == 1 ? "the column already contains duplicate values" : "existing rows already share the same values of those columns";
            var message = $"Cannot create unique index {indexName} on [{table.TableName}] ({columns}): {already}. " +
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
