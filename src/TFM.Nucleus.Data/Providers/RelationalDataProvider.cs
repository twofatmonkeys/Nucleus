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
        }

        /// <summary>The provider's current options.</summary>
        public abstract RelationalProviderOptions Options { get; }

        public NucleusOptions NucleusOptions => _nucleusOptions.CurrentValue;

        /// <summary>Writes this provider's queries in its dialect.</summary>
        public abstract IQueryRenderer Renderer { get; }

        /// <summary>The prefix of a bound parameter's name in command text.</summary>
        public virtual string ParameterPrefix => "@";

        /// <summary>The engine, for log and error messages.</summary>
        protected abstract string EngineName { get; }

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

        /// <summary>The provider's exception for a failure of the given kind.</summary>
        protected abstract Exception ProviderException(DataProviderErrorKind kind, string? message = null);

        /// <summary>The logger the schema manager and the provider's schema extras write to.</summary>
        protected ILogger<SchemaManager>? SchemaLogger => _schemaLogger;

        /// <summary>The tables this provider stores and their state in the database.</summary>
        public SchemaManager Schema => _schema ??= new SchemaManager(SchemaStore, _objectSchemaProvider, _schemaLogger);

        public DataProviderStatus Status => MapSchemaStatus(Schema.Status);

        public Guid ConnectionIdentifier => Options.ID;

        public Type? RootType { get; set; }

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
                if (schemaStatus != EDataSchemaStatus.Ok)
                {
                    _logger?.LogError("Error initializing the {engine} data schema.", EngineName);
                    throw schemaStatus switch
                    {
                        EDataSchemaStatus.DatasourceNotFound => ProviderException(DataProviderErrorKind.DatabaseNonExistent, "Database not found during schema initialization."),
                        EDataSchemaStatus.NotCurrent => ProviderException(DataProviderErrorKind.SchemaNotCurrent, "Database schema is not current."),
                        EDataSchemaStatus.NotInitialized => ProviderException(DataProviderErrorKind.NotInitialized, "Database schema is not initialized."),
                        EDataSchemaStatus.Error => ProviderException(DataProviderErrorKind.Error, $"Error initializing the {EngineName} data schema."),
                        _ => ProviderException(DataProviderErrorKind.Unspecified, $"Unspecified error initializing the {EngineName} data schema."),
                    };
                }

                return DataProviderStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing the {engine} data provider", EngineName);
                return DataProviderStatus.Error;
            }
        }

        /// <summary>Initialises on first use and refuses to hand out a context while the provider is not ready.</summary>
        protected void EnsureReady()
        {
            if (Status == DataProviderStatus.Uninitialized)
                Initialize();
            if (Status != DataProviderStatus.Ok)
                throw ProviderException(DataProviderErrorKind.NotInitialized);
        }

        /// <inheritdoc cref="EnsureReady"/>
        protected async Task EnsureReadyAsync()
        {
            if (Status == DataProviderStatus.Uninitialized)
                await InitializeAsync();
            if (Status != DataProviderStatus.Ok)
                throw ProviderException(DataProviderErrorKind.NotInitialized);
        }

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
                var ones = new SelectQuery(Renderer, delete.TableSchema, Schema.GetTableSchema) { Where = delete.Where };
                ones.LinkedQueries.Clear();
                var members = new SelectQuery(Renderer, membersTable, Schema.GetTableSchema);
                members.LinkedQueries.Clear();
                members.Columns = [membersTable.IdentityColumn?.ColumnName ?? nameof(ICoreObject.ID)];
                ones.AddChildQuery(members, membersTable.FindByPropertyName(key.Name)?.ColumnName ?? key.Name);
                yield return (collection, members);
            }
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
        /// A removed child or a link whose type this provider does not store throws before
        /// anything runs. Each delete is a top-level <see cref="DeleteWhereQuery"/>, so the context
        /// runs it on its delete path, never regrouped with the writes.
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
            foreach (var links in graph.Links.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(links.Key)
                    ?? throw new NotSupportedException(
                        $"A [LinkedObject(forceSave: true)] property holds a {links.Key.Name}, which this provider does not store, so the save cannot write it " +
                        "in the same transaction. Save it through its own provider, or drop forceSave.");
                queries.AddRange(WriteQueries(table, [.. links], separateOperations));
            }
            foreach (var group in roots.GroupBy(rootTable))
                queries.AddRange(WriteQueries(group.Key, [.. group], separateOperations));
            // After the roots, so a member is never written before the one its key points at.
            foreach (var members in graph.Members.GroupBy(o => o.GetType()))
            {
                var table = Schema.GetTableSchema(members.Key)
                    ?? throw new NotSupportedException(
                        $"A linked collection holds new or changed {members.Key.Name} members, which this provider does not store, so the save cannot write them " +
                        "in the same transaction. Save them through their own provider.");
                queries.AddRange(WriteQueries(table, [.. members], separateOperations));
            }
            return queries;
        }

        /// <summary>
        /// The inserts and updates of objects stored in one table: the updates linked from the
        /// inserts, or as separate chains.
        /// </summary>
        private IEnumerable<Query> WriteQueries(TableSchema table, List<ICoreObject> objs, bool separateOperations)
        {
            foreach (var obj in objs)
                Bind(obj);
            var update = UpdateQuery.Create(Renderer, table, objs.Where(x => !x.IsNew));
            var insert = InsertQuery.Create(Renderer, table, objs.Where(x => x.IsNew));
            if (separateOperations)
                return new Query?[] { insert, update }.OfType<Query>();
            if (insert != null)
            {
                if (update != null)
                    insert.LinkedQueries.Add(update);
                return [insert];
            }
            return update != null ? [update] : [];
        }

        public TableSchema GetTableSchema(Type type) =>
            Schema.GetTableSchema(type) ?? throw new Exception($"Error retrieving the table schema for type: {type.FullName}, not found.");

        public TableSchema GetTableSchema<T>() => GetTableSchema(typeof(T));

        #endregion

        #region Failures

        /// <summary>
        /// Wraps a database failure with a message: a unique violation becomes a
        /// <see cref="UniqueViolationException"/> naming the entity and the column or [TableIndex]
        /// of a Nucleus index, anything else a plain exception. The original is the inner exception
        /// either way.
        /// </summary>
        public Exception TranslateFailure(string message, Exception exception)
        {
            if (TranslateEngineFailure(message, exception) is { } translated)
                return translated;
            if (!TryGetUniqueViolation(exception, out var physicalIndexName))
                return new Exception(message, exception);
            return UniqueIndexResolver.Resolve(Schema.TableCache.Values, SchemaStore.Dialect, physicalIndexName) switch
            {
                null => new UniqueViolationException(message, physicalIndexName, null, null, null, exception),
                { DeclaredIndexName: { } declared } index =>
                    new UniqueViolationException(message, index.IndexName, index.EntityType, declared, index.PropertyNames, index.ColumnNames, exception),
                var index => new UniqueViolationException(message, index.IndexName, index.EntityType, index.PropertyNames[0], index.ColumnNames[0], exception),
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
            return CreateDataContext(options.Parent, options.UseTransaction, options.UseDependency);
        }

        Task<IDataContext> IDataProvider.CreateContextAsync(ContextOptions? options, CancellationToken token)
        {
            options ??= ContextOptions.Default;
            return CreateDataContextAsync(options.Parent, options.UseTransaction, options.UseDependency, token);
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

        DataProviderStatus IDataProvider.CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(Schema.CheckSchema());
        async Task<DataProviderStatus> IDataProvider.CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(await Schema.CheckSchemaAsync());

        void IDataProvider.UpdateSchema(IEnumerable<IObjectSchema> objectSchemas) => Schema.Update();
        Task IDataProvider.UpdateSchemaAsync(IObjectSchema objectSchema) => Schema.UpdateAsync();
        Task IDataProvider.UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas) => Schema.UpdateAsync();

        private static DataProviderStatus MapSchemaStatus(EDataSchemaStatus status) => status switch
        {
            EDataSchemaStatus.Ok => DataProviderStatus.Ok,
            EDataSchemaStatus.NotCurrent => DataProviderStatus.IncompatibleSchema,
            EDataSchemaStatus.NotInitialized => DataProviderStatus.Uninitialized,
            EDataSchemaStatus.DatasourceNotFound => DataProviderStatus.NotAvailable,
            EDataSchemaStatus.Error => DataProviderStatus.Error,
            _ => DataProviderStatus.Unknown,
        };
        #endregion
    }

    /// <summary>A provider could not be configured, initialised or reached.</summary>
    public class DataProviderException : Exception
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
        DatabaseUnavailable = 20,
        DatabaseNonExistent = 21,
        Error = 90,
        Unspecified = 99,
    }
}
