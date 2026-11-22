using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;

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

        /// <summary>
        /// True when the provider's rows of a type are its own: the registry sends them to it. A
        /// provider can hold tables for types another provider stores, and a key to one of those
        /// would refuse every write. Every type, when not set.
        /// </summary>
        internal Func<Type, bool>? Routes { get; init; }

        /// <summary>The provider keeps foreign keys: the store asks for them and the dialect can write them.</summary>
        private bool KeepsForeignKeys => _store.ForeignKeys && Dialect.SupportsForeignKeys;

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
            BuildForeignKeys(tables);
            return tables;
        }

        /// <summary>
        /// The foreign key of each column that holds another row's ID: a child's parent, a stored
        /// [LinkedObject], the key a [LinkedBy] collection names, and a [ForeignKey(typeof(T))]
        /// column, unless [ForeignKey(false)] drops it. A key is kept only to a table that holds
        /// every row the column can point at: a concrete type with no stored derived types, whose
        /// rows this provider keeps, from a table whose rows it keeps too. A convention key that
        /// cannot hold is left out; a [ForeignKey(typeof(T))] that cannot fails the build. [ForeignKey] mistakes fail the
        /// build whether or not the provider keeps keys.
        /// </summary>
        private void BuildForeignKeys(IReadOnlyList<TableSchema> tables)
        {
            foreach (var table in tables)
            {
                table.ForeignKeys = [];
                table.SoftReferencesIn = [];
                foreach (var column in table.Columns)
                    column.HasForeignKey = false;
            }
            var linkedBy = new Dictionary<(Type Many, string Property), Type>();
            foreach (var table in tables)
                if (table.ObjectSchema.HasLinkedCollections)
                    foreach (var collection in table.ObjectSchema.LinkedCollectionProperties)
                        linkedBy[(collection.LinkedType!, table.ObjectSchema.GetLinkedCollectionKey(collection).Name)] = table.ObjectSchema.Type;

            var keys = new List<ForeignKeySchema>();
            foreach (var table in tables)
            {
                var schema = table.ObjectSchema;
                foreach (var property in schema.AllProperties)
                    if (DeclaredKey(property) is not null && table.FindByPropertyName(property.Name) is null)
                        throw new InvalidOperationException(
                            $"{schema.Name}.{property.Name} has [ForeignKey] but is not a stored column. Give it [DatabaseColumn], or remove [ForeignKey].");
                foreach (var column in table.Columns)
                {
                    var property = column.PropertySchema;
                    var declared = DeclaredKey(property);
                    var (target, source) = property.IsParentObject ? (property.Type, ForeignKeySource.Parent)
                        : property.IsLinkedObject ? (property.Type, ForeignKeySource.LinkedObject)
                        : linkedBy.TryGetValue((schema.Type, property.Name), out var one) ? (one, ForeignKeySource.LinkedBy)
                        : ((Type?)null, ForeignKeySource.Declared);
                    if (declared != null)
                    {
                        var where = $"{schema.Name}.{property.Name}";
                        if (!declared.Enabled)
                        {
                            if (source == ForeignKeySource.Parent)
                                throw new InvalidOperationException($"{where} is a child's parent, whose key is always kept. Remove [ForeignKey(false)].");
                            continue;
                        }
                        if (declared.ReferencedType is { } named)
                        {
                            if (target != null && target != named)
                                throw new InvalidOperationException(
                                    $"{where} holds a {target.Name}, but its [ForeignKey] names {named.Name}. Name the type the property holds, or use [ForeignKey] without a type.");
                            (target, source) = (named, target == null ? ForeignKeySource.Declared : source);
                        }
                        if (target == null)
                            throw new InvalidOperationException(
                                $"{where} has [ForeignKey] without a type, but it is not a reference Nucleus knows. Name the type it holds the ID of: [ForeignKey(typeof(...))].");
                        if (column.Intent != ColumnTypeIntent.Guid)
                            throw new InvalidOperationException($"{where} has [ForeignKey], but a key column holds an ID, which is a Guid.");
                        if (declared.OnDelete == ForeignKeyAction.SetNull && source == ForeignKeySource.Parent)
                            throw new InvalidOperationException($"{where} is a child's parent, which cannot be set to null when its parent is deleted.");
                    }
                    if (target == null || !KeepsForeignKeys)
                        continue;
                    if (WhyNoKey(table, target, tables, out var referenced) is { } reason)
                    {
                        // A key named by hand must hold; [ForeignKey] without a type only sets the
                        // options of the key the reference would get.
                        if (declared?.ReferencedType != null)
                            throw new InvalidOperationException($"{schema.Name}.{property.Name} has [ForeignKey] to {target.Name}, which cannot hold: {reason}.");
                        _logger?.LogDebug("No foreign key for {table}.{column}: {reason}.", table.TableName, column.ColumnName, reason);
                        continue;
                    }
                    column.HasForeignKey = true;
                    keys.Add(new ForeignKeySchema(table, column, referenced!, declared?.OnDelete ?? ForeignKeyAction.NoAction, source));
                }
            }
            foreach (var group in keys.GroupBy(k => k.Table))
                group.Key.ForeignKeys = [.. group];
            // A soft-deleted row keeps its reference; a hard delete of the row it points at clears
            // it first. A key the database sets to null needs nothing.
            foreach (var group in keys.Where(k => k.OnDelete == ForeignKeyAction.NoAction && SoftDelete.Applies(k.Table)).GroupBy(k => k.Referenced))
                group.Key.SoftReferencesIn = [.. group];
        }

        private static ForeignKeyAttribute? DeclaredKey(IPropertySchema property) =>
            (ForeignKeyAttribute?)Attribute.GetCustomAttribute(property.PropertyInfo, typeof(ForeignKeyAttribute), inherit: true);

        /// <summary>Why a key from <paramref name="table"/> to <paramref name="target"/> cannot hold, or null when it can.</summary>
        private string? WhyNoKey(TableSchema table, Type target, IReadOnlyList<TableSchema> tables, out TableSchema? referenced)
        {
            referenced = null;
            if (Routes?.Invoke(table.ObjectSchema.Type) == false)
                return $"another provider keeps the rows of {table.ObjectSchema.Name}";
            if (target.IsAbstract || target.IsInterface)
                return $"{target.Name} is abstract, so the rows it can point at are in the tables of the types derived from it";
            if (!TableCache.TryGetValue(target, out referenced) || Routes?.Invoke(target) == false)
            {
                referenced = null;
                return $"another provider keeps the rows of {target.Name}";
            }
            var derived = tables.FirstOrDefault(t => t.ObjectSchema.Type != target && target.IsAssignableFrom(t.ObjectSchema.Type));
            if (derived != null)
            {
                referenced = null;
                return $"{derived.ObjectSchema.Name} derives from {target.Name} and is stored in a table of its own, so the column can point at either table";
            }
            return null;
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
                if (Dialect.SupportsForeignKeys && !await ForeignKeysAreCurrent(tables, async, cancellationToken))
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
                await DropKeysOfTablesToChange(transaction, tables, async, cancellationToken);
                await AdoptLegacyTables(transaction, tables, async, cancellationToken);
                // After the adoptions, whose compare starts each adopted table afresh.
                await MarkTablesWhoseInlineKeysChange(tables, async, cancellationToken);
                List<TableSchema> rebuilt = [];
                foreach (var table in tables.Where(x => !x.IsCurrent))
                {
                    try
                    {
                        if (!await TryAddColumns(transaction, table, async, cancellationToken))
                        {
                            if (table.ExistsInDatabase)
                                rebuilt.Add(table);
                            await RebuildTable(transaction, table, async, cancellationToken);
                        }
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
                // After the indexes, so every key column is indexed before its key is checked.
                if (Dialect.SupportsForeignKeys && Dialect.ForeignKeysInline)
                    await ReportOrphansOfRebuiltTables(rebuilt, async, cancellationToken);
                else if (Dialect.SupportsForeignKeys)
                    await SyncForeignKeys(tables, async, cancellationToken);
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

        /// <summary>
        /// True when every table has exactly the Nucleus-named keys it needs. A key whose existing
        /// rows were not checked when it was created still counts, with a warning each start.
        /// </summary>
        private async ValueTask<bool> ForeignKeysAreCurrent(IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var dbKeys = await _store.Catalog.ReadForeignKeysAsync(async, cancellationToken);
            var current = true;
            foreach (var table in tables)
            {
                var (drop, create) = SchemaComparer.DiffForeignKeys(table, dbKeys, Dialect);
                if (drop.Count > 0 || create.Count > 0)
                {
                    _logger?.LogWarning("Foreign key schema is not current for table: {tablename}. Missing or changed: {missing}. Unexpected: {unexpected}",
                        table.TableName, string.Join(", ", create.Select(k => Dialect.PhysicalName(k.Name))), string.Join(", ", drop));
                    current = false;
                }
            }
            foreach (var key in dbKeys.Where(k => !k.IsChecked))
                _logger?.LogWarning("Foreign key {key} on table {tablename} does not cover the rows that were there when it was created: some point at rows that do not exist. " +
                    "Rows written since are checked.", key.KeyName, key.TableName);
            return current;
        }

        /// <summary>
        /// Drops, in the schema transaction, every Nucleus-named key from or to a table the update
        /// is about to change or adopt. A rebuild renames the table and drops it after the copy,
        /// and a key that went with the renamed table would stop the drop (or keep pointing at
        /// it). The keys are created again after the commit.
        /// </summary>
        private async ValueTask DropKeysOfTablesToChange(ISchemaTransaction transaction, IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            // Inline keys go with their table's own definition, and are replaced with it.
            if (!Dialect.SupportsForeignKeys || Dialect.ForeignKeysInline)
                return;
            var changing = new HashSet<string>(Dialect.IdentifierComparer);
            foreach (var table in tables.Where(t => !t.IsCurrent))
            {
                changing.Add(Dialect.PhysicalName(table.TableName));
                foreach (var candidate in AdoptionCandidates(table))
                    changing.Add(Dialect.PhysicalName(candidate));
            }
            if (changing.Count == 0)
                return;
            foreach (var key in await _store.Catalog.ReadForeignKeysAsync(async, cancellationToken))
                if (changing.Contains(key.TableName!) || changing.Contains(key.ReferencedTableName!))
                {
                    _logger?.LogInformation("Dropping foreign key {key} on table {tablename} while its tables change.", key.KeyName, key.TableName);
                    await transaction.ExecuteAsync(Dialect.DropForeignKey(key.TableName!, key.KeyName!), async, cancellationToken);
                }
        }

        /// <summary>
        /// On an engine that keeps keys only with their table, marks for a rebuild each existing
        /// table whose keys differ from what the schema needs: a key it lacks on a column it already
        /// has, or one it has that is not needed or has changed. A key on a column the update adds
        /// is written with the column, so it needs no rebuild. Read from the catalog as it stood
        /// before the update, a table about to be adopted under its old name.
        /// </summary>
        private async ValueTask MarkTablesWhoseInlineKeysChange(IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            if (!Dialect.SupportsForeignKeys || !Dialect.ForeignKeysInline)
                return;
            var snapshot = await _store.Catalog.ReadAsync(async, cancellationToken);
            var dbKeys = await _store.Catalog.ReadForeignKeysAsync(async, cancellationToken);
            var comparer = Dialect.IdentifierComparer;
            foreach (var table in tables)
            {
                var physical = Dialect.PhysicalName(table.TableName);
                var source = snapshot.Tables.Contains(physical, comparer) ? physical
                    : PresentAdoptionCandidates(table, snapshot) is [var adopted] ? Dialect.PhysicalName(adopted)
                    : null;
                if (source == null)
                    continue;
                // The keys and columns of the table as it is stored, under the name it will have.
                var stored = dbKeys.Where(k => comparer.Equals(k.TableName, source)).Select(k => new CatalogForeignKey
                {
                    TableName = physical,
                    KeyName = $"{IndexNaming.ForeignKeyPrefix}{table.TableName}_{k.ColumnName}",
                    ColumnName = k.ColumnName,
                    ReferencedTableName = k.ReferencedTableName,
                    ReferencedColumnName = k.ReferencedColumnName,
                    OnDelete = k.OnDelete,
                    IsChecked = k.IsChecked,
                }).ToList();
                var (drop, create) = SchemaComparer.DiffForeignKeys(table, stored, Dialect);
                var columnExists = (ForeignKeySchema key) => snapshot.Columns.Any(c =>
                    comparer.Equals(c.TableName, source) && comparer.Equals(c.Name, key.Column.ColumnName));
                if (drop.Count > 0 || create.Any(columnExists))
                {
                    _logger?.LogInformation("Rebuilding table {tablename} for its foreign keys: missing or changed {missing}, not needed {unexpected}.",
                        table.TableName, string.Join(", ", create.Select(k => k.Name)), string.Join(", ", drop));
                    table.RebuildForKeys = true;
                }
            }
        }

        /// <summary>
        /// On an engine that keeps keys only with their table, a rebuild copies the rows without
        /// checking them against the keys. Counts, for each key of each rebuilt table, the rows that
        /// point at missing rows, and reports them as a warning: Nucleus never changes rows to make
        /// a key fit, and rows written after it are checked.
        /// </summary>
        private async ValueTask ReportOrphansOfRebuiltTables(IReadOnlyList<TableSchema> rebuilt, bool async, CancellationToken cancellationToken)
        {
            foreach (var key in rebuilt.SelectMany(t => t.ForeignKeys))
            {
                var orphans = await _store.Catalog.ReadCountAsync(Dialect.CountOrphans(key), async, cancellationToken);
                if (orphans > 0)
                    _logger?.LogWarning("Foreign key {key} on table {tablename} does not cover {count} existing rows that point at rows of {referenced} " +
                        "that do not exist. Rows written from now on are checked. Remove or correct those rows to have the key cover every row.",
                        key.Name, key.Table.TableName, orphans, key.Referenced.TableName);
            }
        }

        /// <summary>
        /// Brings each table's keys in line with the entity model: drops the Nucleus-named keys it
        /// no longer needs or whose definition changed, then creates the missing ones. A key whose
        /// table already holds rows pointing at missing rows is created without checking them, and
        /// the count is logged as a warning: Nucleus never changes rows to make a key fit.
        /// </summary>
        private async ValueTask SyncForeignKeys(IReadOnlyList<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var dbKeys = await _store.Catalog.ReadForeignKeysAsync(async, cancellationToken);
            List<ForeignKeySchema> toCreate = [];
            foreach (var table in tables)
            {
                var (drop, create) = SchemaComparer.DiffForeignKeys(table, dbKeys, Dialect);
                foreach (var keyName in drop)
                {
                    _logger?.LogInformation("Dropping foreign key {key} on table {tablename}.", keyName, table.TableName);
                    await _store.Catalog.ExecuteAsync(Dialect.DropForeignKey(Dialect.PhysicalName(table.TableName), keyName), async, cancellationToken);
                }
                toCreate.AddRange(create);
            }
            // Every drop first: a changed key keeps its name, and a table can be both.
            foreach (var key in toCreate)
            {
                var orphans = await _store.Catalog.ReadCountAsync(Dialect.CountOrphans(key), async, cancellationToken);
                if (orphans > 0)
                    _logger?.LogWarning("Creating foreign key {key} on table {tablename} without checking its {count} existing rows that point at rows of {referenced} " +
                        "that do not exist. Rows written from now on are checked. Remove or correct those rows to have the key cover every row.",
                        Dialect.PhysicalName(key.Name), key.Table.TableName, orphans, key.Referenced.TableName);
                else
                    _logger?.LogInformation("Creating foreign key {key} on table {tablename}.", Dialect.PhysicalName(key.Name), key.Table.TableName);
                await _store.Catalog.ExecuteAsync(Dialect.AddForeignKey(key, checkExisting: orphans == 0), async, cancellationToken);
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
