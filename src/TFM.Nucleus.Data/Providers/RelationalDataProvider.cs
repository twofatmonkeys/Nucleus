using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The <see cref="IDataProvider"/> every relational provider shares: status and
    /// initialisation through a <see cref="SchemaManager"/>, the query factories (each query
    /// carries the provider's <see cref="Renderer"/>), table lookup, context creation and the
    /// translation of a database failure into a neutral exception. A provider supplies its
    /// options, renderer, schema store, contexts, capabilities and change subscriptions.
    /// </summary>
    public abstract class RelationalDataProvider : IDataProvider
    {
        private readonly IOptionsMonitor<NucleusOptions> _nucleusOptions;
        private readonly ILogger? _logger;
        private readonly ILogger<SchemaManager>? _schemaLogger;
        private IObjectSchemaProvider _objectSchemaProvider;
        private SchemaManager? _schema;

        protected RelationalDataProvider(IObjectSchemaProvider objectSchemaProvider, IOptionsMonitor<NucleusOptions> nucleusOptions,
            ILogger? logger, ILogger<SchemaManager>? schemaLogger)
        {
            _objectSchemaProvider = objectSchemaProvider;
            _nucleusOptions = nucleusOptions;
            _logger = logger;
            _schemaLogger = schemaLogger;
            ConnectionSlots = new ConnectionSlots(this);
        }

        /// <summary>The provider's current options.</summary>
        public abstract RelationalProviderOptions Options { get; }

        /// <summary>
        /// This provider's connection budget, shared by its open root contexts and its live change
        /// subscriptions. A subscription takes its slot before it opens its connection.
        /// </summary>
        public ConnectionSlots ConnectionSlots { get; }

        /// <summary>The provider's logger, which its contexts report through.</summary>
        internal ILogger? Logger => _logger;

        public NucleusOptions NucleusOptions => _nucleusOptions.CurrentValue;

        /// <summary>Writes this provider's queries in its dialect.</summary>
        public abstract IQueryRenderer Renderer { get; }

        /// <summary>The prefix of a bound parameter's name in command text.</summary>
        public virtual string ParameterPrefix => "@";

        /// <summary>The engine, for log and error messages.</summary>
        protected abstract string EngineName { get; }

        /// <summary><see cref="EngineName"/>, for the messages of this provider's contexts.</summary>
        internal string EngineDisplayName => EngineName;

        /// <summary>The dialect and database access the schema manager uses.</summary>
        protected abstract ISchemaStore SchemaStore { get; }

        public abstract DataProviderCapabilities Capabilities { get; }

        protected abstract IDataContext CreateDataContext(IDataContext? parent, bool useTransaction, bool useDependency);

        protected abstract Task<IDataContext> CreateDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token);

        public abstract Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new();

        /// <summary>
        /// True when the exception, or one in its inner chain, is the engine's unique violation;
        /// gives the name of the broken index when the engine reports one.
        /// </summary>
        protected abstract bool TryGetUniqueViolation(Exception exception, out string? physicalIndexName);

        /// <summary>
        /// True when the exception, or one in its inner chain, is the engine's foreign key
        /// violation; gives the name of the broken key when the engine reports one. The default
        /// finds none, so a provider that predates foreign keys reports a plain failure.
        /// </summary>
        protected virtual bool TryGetForeignKeyViolation(Exception exception, out string? physicalKeyName)
        {
            physicalKeyName = null;
            return false;
        }

        /// <summary>
        /// What the engine's failure is, read from the driver's exception anywhere in the chain:
        /// its kind, and whether it can pass on another attempt. It also decides which failures
        /// opening a connection retries (<see cref="RelationalProviderOptions.ConnectAttempts"/>).
        /// The default knows only timeouts and the driver's own transient flag
        /// (<see cref="DatabaseFailures.ClassifyDefault"/>); a provider names its engine's errors.
        /// </summary>
        protected internal virtual DatabaseErrorClass ClassifyFailure(Exception exception) => DatabaseFailures.ClassifyDefault(exception);

        /// <summary>The provider's exception for a failure of the given kind.</summary>
        protected abstract Exception ProviderException(DataProviderErrorKind kind, string? message = null);

        /// <summary>The logger the schema manager and the provider's schema extras write to.</summary>
        protected ILogger<SchemaManager>? SchemaLogger => _schemaLogger;

        /// <summary>The tables this provider stores and their state in the database.</summary>
        public SchemaManager Schema => _schema ??= new SchemaManager(SchemaStore, _objectSchemaProvider, _schemaLogger) { Routes = RoutesHere };

        /// <summary>
        /// The registry this provider is one of, set when the registry is built. It decides which
        /// types the provider's rows are read and written for, which can be fewer than the types
        /// it has tables for.
        /// </summary>
        internal IDataProviderRegistry? Registry { get; set; }

        /// <summary>
        /// True when the registry sends <paramref name="type"/>'s rows to this provider: a key
        /// between two of its tables holds only then. Without a registry, every type under the
        /// provider's root.
        /// </summary>
        private bool RoutesHere(Type type)
        {
            if (Registry is not { } registry)
                return DataProviderRegistry.IsUnderRoot(type, RootType);
            try
            {
                return registry.IsOwnedBy(type, this);
            }
            catch (InvalidOperationException)
            {
                // No provider takes the type.
                return false;
            }
        }

        public DataProviderStatus Status => MapSchemaStatus(Schema.Status);

        public Guid ConnectionIdentifier => Options.ID;

        public Type? RootType { get; set; }

        /// <inheritdoc cref="RelationalProviderOptions.RetryAttempts"/>
        public int RetryAttempts => Math.Max(1, Options.RetryAttempts);

        #region Initialisation

        public DataProviderStatus Initialize(IObjectSchemaProvider? objectSchema = null) =>
            InitializeCore(objectSchema, async: false, CancellationToken.None).GetAwaiter().GetResult();

        public Task<DataProviderStatus> InitializeAsync(IObjectSchemaProvider? objectSchema = null, CancellationToken? cancellationToken = null) =>
            InitializeCore(objectSchema, async: true, cancellationToken ?? CancellationToken.None).AsTask();

        private async ValueTask<DataProviderStatus> InitializeCore(IObjectSchemaProvider? objectSchema, bool async, CancellationToken cancellationToken)
        {
            if (Status == DataProviderStatus.Ok)
                return Status;
            try
            {
                if (objectSchema != null)
                {
                    _logger?.LogInformation("Initializing the {engine} data provider with the provided object schema.", EngineName);
                    _objectSchemaProvider = objectSchema;
                    _schema = null;
                }
                var schemaStatus = async ? await Schema.InitializeAsync(cancellationToken) : Schema.Initialize();
                if (schemaStatus != DataSchemaStatus.Ok)
                {
                    _logger?.LogError("Error initializing the {engine} data schema.", EngineName);
                    throw schemaStatus switch
                    {
                        DataSchemaStatus.DatasourceNotFound => ProviderException(DataProviderErrorKind.DatabaseNonExistent, "Database not found during schema initialization."),
                        DataSchemaStatus.NotCurrent => ProviderException(DataProviderErrorKind.SchemaNotCurrent, "Database schema is not current."),
                        DataSchemaStatus.NotInitialized => ProviderException(DataProviderErrorKind.NotInitialized, "Database schema is not initialized."),
                        DataSchemaStatus.Error => ProviderException(DataProviderErrorKind.Error, $"Error initializing the {EngineName} data schema."),
                        _ => ProviderException(DataProviderErrorKind.Unspecified, $"Unspecified error initializing the {EngineName} data schema."),
                    };
                }

                _initializationFailure = null;
                return DataProviderStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing the {engine} data provider", EngineName);
                _initializationFailure = ex;
                return DataProviderStatus.Error;
            }
        }

        /// <summary>
        /// Why the last initialisation failed, kept so that a caller refused a context is told the
        /// reason and not only that the provider is not ready. Null once one succeeds.
        /// </summary>
        private volatile Exception? _initializationFailure;

        /// <summary>Initialises on first use and refuses to hand out a context while the provider is not ready.</summary>
        protected void EnsureReady()
        {
            if (Status == DataProviderStatus.Uninitialized)
                Initialize();
            if (Status != DataProviderStatus.Ok)
                throw NotReady();
        }

        /// <inheritdoc cref="EnsureReady"/>
        protected async Task EnsureReadyAsync()
        {
            if (Status == DataProviderStatus.Uninitialized)
                await InitializeAsync();
            if (Status != DataProviderStatus.Ok)
                throw NotReady();
        }

        /// <summary>
        /// The exception for a provider that is not ready, from why its initialisation failed. A
        /// database that failed is a <see cref="DatabaseException"/> like any other failure of it,
        /// one level over the driver's exception; a schema or deployment that is wrong keeps its
        /// <see cref="DataProviderErrorKind"/>; anything else is
        /// <see cref="DataProviderErrorKind.NotInitialized"/> with the reason in its message. Each
        /// is built new, because the kept failure may be reported to many callers at once.
        /// </summary>
        private Exception NotReady() => _initializationFailure switch
        {
            DatabaseException database => new DatabaseException(
                $"The {EngineName} data provider could not be initialised. {database.Message}",
                database.Kind, database.IsTransient, database.InnerException)
            {
                Operation = database.Operation,
                Attempts = database.Attempts,
            },
            DataProviderException provider => ProviderException(provider.Kind, provider.Message),
            { } other => ProviderException(DataProviderErrorKind.NotInitialized,
                $"The {EngineName} data provider could not be initialised. {other.Message}"),
            null => ProviderException(DataProviderErrorKind.NotInitialized),
        };

        #endregion

        #region Queries and tables

        public SelectQuery ParseSelectExpression<T>(Expression expression)
        {
            var tblSchema = Schema.GetTableSchema<T>() ?? throw new Exception($"Error retrieving the table schema for type: {typeof(T).FullName}, not found.");
            var query = new SelectQuery(Renderer, tblSchema, Schema.GetTableSchema);
            return query.Parse(expression);
        }

        public UpdateQuery? CreateUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            UpdateQuery.Create(Renderer, GetTableSchema<T>(), Bind(objs));

        public InsertQuery? CreateInsertQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            InsertQuery.Create(Renderer, GetTableSchema<T>(), Bind(objs));

        /// <summary>
        /// Makes this provider the object's data provider, which its child collections build
        /// their write queries through. Every write query is built after it, so a loaded object
        /// saves its children on any save path, and their children in turn.
        /// </summary>
        internal void Bind(ICoreObject obj) => obj.DataProvider = this;

        private List<T>? Bind<T>(IEnumerable<T>? objs) where T : ICoreObject
        {
            if (objs == null)
                return null;
            var list = objs.ToList();
            foreach (var obj in list)
                Bind(obj);
            return list;
        }

        /// <summary>
        /// Deletes the given objects by ID, with every row of their child collections, whether
        /// or not those were loaded. Null when there is nothing to delete.
        /// </summary>
        public DeleteWhereQuery? CreateDeleteQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null)
                return null;
            return CreateDeleteByIdQuery(GetTableSchema<T>(), objs.Where(o => o != null && !o.IsNew).Select(o => o.ID));
        }

        /// <summary>Deletes rows of one table by ID, cascading to child collection rows.</summary>
        public DeleteWhereQuery? CreateDeleteByIdQuery(TableSchema tableSchema, IEnumerable<Guid> ids) =>
            DeleteWhereQuery.ForIds(Renderer, tableSchema, Schema.GetTableSchema, ids);

        /// <summary>
        /// Deletes the rows of <typeparamref name="T"/> matching a predicate body, cascading to
        /// child collection rows. The caller checks the predicate with
        /// <see cref="ExpressionParser.CheckDeletePredicate"/> first.
        /// </summary>
        public DeleteWhereQuery CreateDeleteWhereQuery<T>(Expression predicate) where T : ICoreObject<T>, new()
        {
            var query = new DeleteWhereQuery(Renderer, GetTableSchema<T>(), Schema.GetTableSchema);
            return (DeleteWhereQuery)ExpressionParser.ParseExpressionToQuery(query, predicate);
        }

        /// <summary>
        /// For each linked collection of the type a delete removes rows of, a select of the members
        /// of exactly the rows it matches: the many type's IDs whose key is in the IDs the delete's
        /// own condition selects. A delete is refused while any comes back. Empty for a type with
        /// no linked collections.
        /// </summary>
        internal IEnumerable<(IPropertySchema Collection, SelectQuery Members)> LinkedMembersOf(DeleteWhereQuery delete)
        {
            if (delete.TableSchema?.ObjectSchema is not { HasLinkedCollections: true } schema)
                yield break;
            foreach (var collection in schema.LinkedCollectionProperties)
            {
                var key = schema.GetLinkedCollectionKey(collection);
                var membersTable = Schema.GetTableSchema(collection.LinkedType!)
                    ?? throw new NotSupportedException(
                        $"{schema.Name}.{collection.Name} holds {collection.LinkedType!.Name}, which this provider does not store, so a delete of {schema.Name} cannot check for its members.");
                var members = MembersQuery(Renderer, delete.TableSchema, ones => ones.Where = delete.Where, membersTable,
                    membersTable.FindByPropertyName(key.Name)?.ColumnName ?? key.Name, Schema.GetTableSchema);
                yield return (collection, members);
            }
        }

        /// <summary>
        /// The select of the live members of the rows of <paramref name="oneTable"/> that
        /// <paramref name="condition"/> matches: the IDs of the <paramref name="membersTable"/> rows
        /// whose <paramref name="keyColumn"/> holds one of them. Both reads wait for writers, so a
        /// member another transaction is inserting counts once it commits, on an engine that reads
        /// the last committed version by default.
        /// </summary>
        internal static SelectQuery MembersQuery(IQueryRenderer renderer, IDataTableSchema oneTable, Action<SelectQuery> condition,
            TableSchema membersTable, string keyColumn, Func<Type, IDataTableSchema?> tables)
        {
            var ones = new SelectQuery(renderer, oneTable, tables) { WaitForPendingWrites = true };
            ones.LinkedQueries.Clear();
            condition(ones);
            var members = new SelectQuery(renderer, membersTable, tables) { WaitForPendingWrites = true };
            members.LinkedQueries.Clear();
            members.Columns = [membersTable.IdentityColumn?.ColumnName ?? nameof(ICoreObject.ID)];
            ones.AddChildQuery(members, keyColumn);
            return members;
        }

        public Query? CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            var list = Bind(objs);
            if (list == null || list.Count == 0) return null;
            return WriteQueries(GetTableSchema<T>(), list.Cast<ICoreObject>().ToList(), separateOperations: false).SingleOrDefault();
        }

        /// <inheritdoc cref="IDataProvider.CreateSaveQueries{T}(IEnumerable{T})"/>
        public List<Query> CreateSaveQueries<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            CreateSaveQueries(objs?.Cast<ICoreObject>(), _ => GetTableSchema<T>(), separateOperations: false);

        /// <summary>
        /// The statements a save of objects of any types runs, each written to its own type's
        /// table. <paramref name="separateOperations"/> gives the inserts and the updates of a
        /// type as separate chains, which is the shape the bulk path batches by.
        /// </summary>
        public List<Query> CreateSaveQueries(IEnumerable<ICoreObject?>? objs, bool separateOperations = false) =>
            CreateSaveQueries(objs?.OfType<ICoreObject>(), o => GetTableSchema(o.GetType()), separateOperations);

        /// <summary>
        /// The one save builder every save path uses. The graph below the objects is walked
        /// once (<see cref="SaveGraph"/>), and the statements come out in the order they run:
        /// <list type="number">
        /// <item>a delete per child type of the stored children removed from their collections,
        /// by ID, cascading to their own children and keeping each type's hard or soft rule. They
        /// run first, so a replacement with the same unique key does not collide with the row it
        /// replaces;</item>
        /// <item>a delete per member type of the stored members removed from linked collections,
        /// by ID, as each type allows a direct delete;</item>
        /// <item>the inserts and updates of the forceSave links the save writes, with their children;</item>
        /// <item>the objects' own inserts and updates, with their child collections;</item>
        /// <item>the inserts and updates of the linked collection members the save writes, with
        /// their children.</item>
        /// </list>
        /// The writes of the last three are then put in the order their rows point at each
        /// other (<see cref="SchemaManager.References"/>): a type is written after the types its
        /// rows hold the IDs of, so a link that points at a link, or a root at another root, is
        /// written second. A removed child or a link whose type this provider does not store
        /// throws before anything runs. Each delete is a top-level <see cref="DeleteWhereQuery"/>,
        /// so the context runs it on its delete path, never regrouped with the writes.
        /// </summary>
        private List<Query> CreateSaveQueries(IEnumerable<ICoreObject>? objs, Func<ICoreObject, TableSchema> rootTable, bool separateOperations)
        {
            var roots = objs?.ToList() ?? [];
            if (roots.Count == 0)
                return [];
            foreach (var root in roots)
                Bind(root);
            var graph = SaveGraph.Walk(roots);
            var queries = new List<Query>();
            foreach (var removed in graph.Removed.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(removed.Key)
                    ?? throw new NotSupportedException(
                        $"{removed.Key.Name} children were removed from a collection, but this provider does not store {removed.Key.Name}, so the save cannot delete them.");
                if (DeleteWhereQuery.ForRemovedChildren(Renderer, table, Schema.GetTableSchema, removed.Select(o => o.ID)) is { } delete)
                    queries.Add(delete);
            }
            foreach (var removed in graph.RemovedMembers.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(removed.Key)
                    ?? throw new NotSupportedException(
                        $"{removed.Key.Name} members were removed from a linked collection, but this provider does not store {removed.Key.Name}, so the save cannot delete them.");
                if (DeleteWhereQuery.ForIds(Renderer, table, Schema.GetTableSchema, removed.Select(o => o.ID)) is { } delete)
                    queries.Add(delete);
            }
            var writes = new List<(Type Type, IEnumerable<Query> Queries)>();
            foreach (var links in graph.Links.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(links.Key)
                    ?? throw new NotSupportedException(
                        $"A [LinkedObject(forceSave: true)] property holds a {links.Key.Name}, which this provider does not store, so the save cannot write it " +
                        "in the same transaction. Save it through its own provider, or drop forceSave.");
                writes.Add((links.Key, WriteQueries(table, [.. links], separateOperations)));
            }
            foreach (var group in roots.GroupBy(rootTable))
                // The objects the save was asked for are written even with no changes of their own.
                writes.Add((group.Key.ObjectSchema.Type, WriteQueries(group.Key, [.. group], separateOperations, writeUnchanged: true)));
            // After the roots, so a member is never written before the one its key points at.
            foreach (var members in graph.Members.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(members.Key)
                    ?? throw new NotSupportedException(
                        $"A linked collection holds new or changed {members.Key.Name} members, which this provider does not store, so the save cannot write them " +
                        "in the same transaction. Save them through their own provider.");
                writes.Add((members.Key, WriteQueries(table, [.. members], separateOperations)));
            }
            // Each type's rows after the rows they point at, whatever order the save met them in.
            foreach (var (_, writesOfType) in Schema.References.ForWrites(writes, static w => w.Type))
                queries.AddRange(writesOfType);
            return queries;
        }

        /// <summary>
        /// The inserts and updates of objects stored in one table, then the writes below the
        /// stored objects that have nothing of their own to write: the updates linked from the
        /// inserts and those writes linked after them, or each as a separate chain.
        /// </summary>
        /// <remarks>
        /// A stored object with no changes of its own (<see cref="SaveGraph.IsChanged"/>) is not
        /// written unless <paramref name="writeUnchanged"/> says so, which only the objects a save
        /// was asked for do. Its update would set nothing but the server's columns, so a parent
        /// save used to stamp the Timestamp of every loaded child. Its child collections are still
        /// written, so a changed grandchild under a clean child is not lost. Those writes come
        /// last: they are all below rows that already exist, so no row they reference is unwritten.
        /// </remarks>
        private IEnumerable<Query> WriteQueries(TableSchema table, List<ICoreObject> objs, bool separateOperations, bool writeUnchanged = false)
        {
            var hasChildren = table.ObjectSchema.HasNucleusChildCollections;
            List<ICoreObject>? inserts = null;
            List<ICoreObject>? updates = null;
            List<Query>? below = null;
            foreach (var obj in objs)
            {
                Bind(obj);
                if (obj.IsNew)
                    (inserts ??= []).Add(obj);
                else if (writeUnchanged || SaveGraph.IsChanged(obj))
                    (updates ??= []).Add(obj);
                else if (hasChildren)
                    foreach (var collection in obj.NucleusChildCollections)
                        if (collection.GetInsertUpdateQuery() is Query query)
                            (below ??= []).Add(query);
            }
            if (inserts != null && table.SelfReferences.Count > 0)
                inserts = ReferencedFirst(inserts, table.SelfReferences);
            var insert = InsertQuery.Create(Renderer, table, inserts);
            var update = UpdateQuery.Create(Renderer, table, updates);
            if (separateOperations)
                return new Query?[] { insert, update }.OfType<Query>().Concat(below ?? []);
            Query? root = null;
            foreach (var query in new Query?[] { insert, update }.OfType<Query>().Concat(below ?? []))
            {
                if (root is null)
                    root = query;
                else
                    root.LinkedQueries.Add(query);
            }
            return root is null ? [] : [root];
        }

        /// <summary>
        /// New rows of one table in an order that inserts each after the rows of the batch it
        /// points at through <paramref name="references"/>, and otherwise as given. Rows that
        /// point at each other in a cycle keep their order. The list itself when no row points at
        /// another row of the batch. A reference is read as it is held, so nothing loads.
        /// </summary>
        internal static List<ICoreObject> ReferencedFirst(List<ICoreObject> inserts, IReadOnlyList<IPropertySchema> references)
        {
            if (inserts.Count < 2)
                return inserts;
            var index = new Dictionary<Guid, int>(inserts.Count);
            for (var i = 0; i < inserts.Count; i++)
                index.TryAdd(inserts[i].ID, i);
            if (!AnyPointsWithin(inserts, references, index))
                return inserts;
            // Depth first, each row placed once the rows it points at are.
            var state = new byte[inserts.Count]; // 0 not reached, 1 waiting, 2 placed
            var ordered = new List<ICoreObject>(inserts.Count);
            var stack = new Stack<(int Row, int Next)>();
            for (var start = 0; start < inserts.Count; start++)
            {
                if (state[start] != 0)
                    continue;
                state[start] = 1;
                stack.Push((start, 0));
                while (stack.Count > 0)
                {
                    var (row, next) = stack.Pop();
                    if (next < references.Count)
                    {
                        stack.Push((row, next + 1));
                        if (PointsAt(inserts[row], references[next], index) is int target && state[target] == 0)
                        {
                            state[target] = 1;
                            stack.Push((target, 0));
                        }
                        continue;
                    }
                    state[row] = 2;
                    ordered.Add(inserts[row]);
                }
            }
            return ordered;
        }

        private static bool AnyPointsWithin(List<ICoreObject> inserts, IReadOnlyList<IPropertySchema> references, Dictionary<Guid, int> index)
        {
            for (var i = 0; i < inserts.Count; i++)
                foreach (var reference in references)
                    if (PointsAt(inserts[i], reference, index) is int target && target != i)
                        return true;
            return false;
        }

        private static int? PointsAt(ICoreObject row, IPropertySchema reference, Dictionary<Guid, int> index) =>
            row.GetPropertyValue(reference.Name) is ICoreObject target && index.TryGetValue(target.ID, out var at) ? at : null;

        public TableSchema GetTableSchema(Type type) =>
            Schema.GetTableSchema(type) ?? throw new Exception($"Error retrieving the table schema for type: {type.FullName}, not found.");

        public TableSchema GetTableSchema<T>() => GetTableSchema(typeof(T));

        #endregion

        #region Failures

        /// <summary>
        /// The exception for a database failure of an operation not recorded, which may have
        /// been applied. See <see cref="TranslateFailure(string, Exception, DatabaseOperation, bool, int)"/>.
        /// </summary>
        public Exception TranslateFailure(string message, Exception exception) =>
            TranslateFailure(message, exception, DatabaseOperation.Unknown, mayHaveBeenApplied: true);

        /// <summary>
        /// The exception for a failure of <paramref name="operation"/>, with the original as its
        /// inner exception: the provider's explanation of an engine failure that needs one, a
        /// <see cref="UniqueViolationException"/> naming the entity and the column or [TableIndex]
        /// of a Nucleus index, or else a <see cref="DatabaseException"/> classified by
        /// <see cref="ClassifyFailure"/>. A failure that is not the database's
        /// (<see cref="DatabaseFailures.IsDatabaseFailure"/>) is returned as it is, for the caller
        /// to rethrow. <paramref name="mayHaveBeenApplied"/> says whether the operation may have
        /// left writes behind; a commit and an open decide it themselves (<see cref="DatabaseFailures.Resolve"/>).
        /// </summary>
        public Exception TranslateFailure(string message, Exception exception, DatabaseOperation operation, bool mayHaveBeenApplied, int attempts = 1)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (TranslateEngineFailure(message, exception) is { } translated)
                return translated;
            if (!DatabaseFailures.IsDatabaseFailure(exception))
                return exception;
            if (TryGetForeignKeyViolation(exception, out var physicalKeyName))
                return ForeignKeyViolation(message, exception, physicalKeyName, operation, mayHaveBeenApplied, attempts);
            if (!TryGetUniqueViolation(exception, out var physicalIndexName))
                return DatabaseFailures.Create(message, exception, ClassifyFailure(exception), operation, mayHaveBeenApplied, attempts);
            var (_, _, applied) = DatabaseFailures.Resolve(new DatabaseErrorClass(DatabaseErrorKind.UniqueViolation, false), operation, mayHaveBeenApplied);
            attempts = Math.Max(1, attempts);
            return UniqueIndexResolver.Resolve(Schema.TableCache.Values, SchemaStore.Dialect, physicalIndexName) switch
            {
                null => new UniqueViolationException(message, physicalIndexName, null, null, null, exception)
                    { Operation = operation, MayHaveBeenApplied = applied, Attempts = attempts },
                { DeclaredIndexName: { } declared } index =>
                    new UniqueViolationException(message, index.IndexName, index.EntityType, declared, index.PropertyNames, index.ColumnNames, exception)
                    { Operation = operation, MayHaveBeenApplied = applied, Attempts = attempts },
                var index => new UniqueViolationException(message, index.IndexName, index.EntityType, index.PropertyNames[0], index.ColumnNames[0], exception)
                    { Operation = operation, MayHaveBeenApplied = applied, Attempts = attempts },
            };
        }

        /// <summary>
        /// The <see cref="ForeignKeyViolationException"/> for a broken key, naming the reference
        /// when the key is one this provider keeps. Resolved from the tables' keys on the failure
        /// path only.
        /// </summary>
        private ForeignKeyViolationException ForeignKeyViolation(string message, Exception exception, string? physicalKeyName,
            DatabaseOperation operation, bool mayHaveBeenApplied, int attempts)
        {
            var (_, _, applied) = DatabaseFailures.Resolve(new DatabaseErrorClass(DatabaseErrorKind.ForeignKeyViolation, false), operation, mayHaveBeenApplied);
            ForeignKeySchema? key = null;
            if (physicalKeyName != null)
            {
                var dialect = SchemaStore.Dialect;
                foreach (var table in Schema.TableCache.Values)
                    foreach (var candidate in table.ForeignKeys)
                        if (dialect.IdentifierComparer.Equals(dialect.PhysicalName(candidate.Name), physicalKeyName))
                            key = candidate;
            }
            return new ForeignKeyViolationException(message, key?.Name ?? physicalKeyName, key?.Table.ObjectSchema.Type,
                key?.Column.PropertyName, key?.Column.ColumnName, key?.Referenced.ObjectSchema.Type, exception)
            {
                Operation = operation,
                MayHaveBeenApplied = applied,
                Attempts = Math.Max(1, attempts),
            };
        }

        /// <summary>
        /// A failure the engine reports that needs explaining in Nucleus's terms, wrapped with
        /// the explanation; null for any other. The default explains nothing.
        /// </summary>
        protected virtual Exception? TranslateEngineFailure(string message, Exception exception) => null;

        #endregion

        #region IDataProvider
        ISelectQuery IDataProvider.ParseSelectExpression<T>(Expression expression) => ParseSelectExpression<T>(expression);
        IUpdateQuery? IDataProvider.CreateUpdateQuery<T>(IEnumerable<T>? objs) => CreateUpdateQuery(objs);
        IInsertQuery? IDataProvider.CreateInsertQuery<T>(IEnumerable<T>? objs) => CreateInsertQuery(objs);
        IQuery? IDataProvider.CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) => CreateInsertAndUpdateQuery(objs);
        IReadOnlyList<IQuery> IDataProvider.CreateSaveQueries<T>(IEnumerable<T>? objs) => CreateSaveQueries(objs);
        IDeleteQuery? IDataProvider.CreateDeleteQuery<T>(IEnumerable<T>? objs) => CreateDeleteQuery(objs);

        IDataContext IDataProvider.CreateContext(ContextOptions? options)
        {
            options ??= ContextOptions.Default;
            return ApplyOptions(CreateDataContext(options.Parent, options.UseTransaction, options.WatchForChanges), options);
        }

        async Task<IDataContext> IDataProvider.CreateContextAsync(ContextOptions? options, CancellationToken token)
        {
            options ??= ContextOptions.Default;
            return ApplyOptions(await CreateDataContextAsync(options.Parent, options.UseTransaction, options.WatchForChanges, token), options);
        }

        /// <summary>The options a context takes once it is open: its statement time limit.</summary>
        private static IDataContext ApplyOptions(IDataContext context, ContextOptions options)
        {
            if (options.StatementTimeoutSeconds is { } seconds && context is IStatementTimeout target)
                target.StatementTimeoutSeconds = seconds;
            return context;
        }

        async Task<DataProviderStatus> IDataProvider.CheckDataContextAsync()
        {
            try
            {
                return await SchemaStore.Catalog.DatabaseExistsAsync(true, CancellationToken.None)
                    ? DataProviderStatus.Ok
                    : DataProviderStatus.NotAvailable;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error probing the {engine} connection", EngineName);
                return DataProviderStatus.NotAvailable;
            }
        }

        DataProviderStatus IDataProvider.Initialize(IEnumerable<IObjectSchema>? objectSchemas) => Initialize();
        Task<DataProviderStatus> IDataProvider.InitializeAsync(IObjectSchemaProvider? objectSchema) => InitializeAsync(objectSchema);

        /// <inheritdoc cref="IDataProvider.CheckSchemaIsCurrent()"/>
        public DataProviderStatus CheckSchemaIsCurrent() => MapSchemaStatus(Schema.CheckSchema());

        /// <inheritdoc cref="IDataProvider.CheckSchemaIsCurrent()"/>
        public async Task<DataProviderStatus> CheckSchemaIsCurrentAsync(CancellationToken cancellationToken = default) =>
            MapSchemaStatus(await Schema.CheckSchemaAsync(cancellationToken));

        /// <inheritdoc cref="IDataProvider.UpdateSchema()"/>
        public void UpdateSchema() => Schema.Update();

        /// <inheritdoc cref="IDataProvider.UpdateSchema()"/>
        public Task UpdateSchemaAsync(CancellationToken cancellationToken = default) => Schema.UpdateAsync(cancellationToken);

        DataProviderStatus IDataProvider.CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas) => CheckSchemaIsCurrent();
        Task<DataProviderStatus> IDataProvider.CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas) => CheckSchemaIsCurrentAsync();

        void IDataProvider.UpdateSchema(IEnumerable<IObjectSchema> objectSchemas) => UpdateSchema();
        Task IDataProvider.UpdateSchemaAsync(IObjectSchema objectSchema) => UpdateSchemaAsync();
        Task IDataProvider.UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas) => UpdateSchemaAsync();

        private static DataProviderStatus MapSchemaStatus(DataSchemaStatus status) => status switch
        {
            DataSchemaStatus.Ok => DataProviderStatus.Ok,
            DataSchemaStatus.NotCurrent => DataProviderStatus.IncompatibleSchema,
            DataSchemaStatus.NotInitialized => DataProviderStatus.Uninitialized,
            DataSchemaStatus.DatasourceNotFound => DataProviderStatus.NotAvailable,
            DataSchemaStatus.Error => DataProviderStatus.Error,
            _ => DataProviderStatus.Unknown,
        };
        #endregion
    }

    /// <summary>A provider could not be configured, initialised or reached.</summary>
    public class DataProviderException : TFM.Nucleus.NucleusException
    {
        public DataProviderErrorKind Kind { get; }

        public DataProviderException(DataProviderErrorKind kind) : base()
        {
            Kind = kind;
        }

        public DataProviderException(string? message, DataProviderErrorKind kind) : base(message)
        {
            Kind = kind;
        }

        public DataProviderException(string? message, DataProviderErrorKind kind, Exception? innerException) : base(message, innerException)
        {
            Kind = kind;
        }
    }

    public enum DataProviderErrorKind
    {
        NotInitialized = 0,
        NotConfigured = 1,
        SchemaNotCurrent = 10,
        DatabaseNonExistent = 21,
        Error = 90,
        Unspecified = 99,
    }
}
