using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus;
using TFM.Nucleus.Abstractions.Interfaces;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The data surface an application or a store codes against, and the one DI registers. Every
    /// operation routes each entity type to the provider of its nearest registered root, runs in a
    /// transaction of its own, evicts the <c>[Cacheable]</c> cache on commit and marks the objects
    /// saved. Reads go through <see cref="Get{T}"/> (entities, by LINQ) and <see cref="Rows{T}"/>
    /// (rows of values).
    /// </summary>
    public interface IObjectDataManager
    {
        IObjectSchemaProvider ObjectSchema { get; }

        /// <summary>The status of the unrooted default provider only.</summary>
        [Obsolete("DataProviderStatus reports the unrooted default provider only. Use Status, which reports the worst status across every provider.")]
        DataProviderStatus DataProviderStatus { get; }
        Task<DataProviderStatus> InitializeDataProviderAsync();
        IObjectDataAdaptor<T> Get<T>() where T : ICoreObject<T>, new();

        Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new();

        /// <summary>
        /// Saves the object with its child collections (deleting the stored children removed from
        /// them) and the forceSave links it reaches, in one transaction. A failure rolls back. A
        /// <see cref="UniqueViolationException"/>, an <see cref="OperationCanceledException"/>, a
        /// <see cref="DataProviderException"/> and a <see cref="DataContextException"/> are thrown
        /// as they are; any other failure is wrapped.
        /// </summary>
        Task SaveAsync<T>(T obj, CancellationToken token) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support a cancellable save.");

        /// <summary>
        /// Saves the objects, as <see cref="SaveAsync{T}(T, CancellationToken)"/> does, all in one
        /// transaction. Returns null when there was nothing to write.
        /// </summary>
        Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support saving a batch.");

        /// <summary>
        /// Deletes the object and its child collection rows at every depth, loaded or not, in one
        /// transaction. The type must be <c>[Deletable]</c>; a new object is skipped.
        /// </summary>
        Task DeleteAsync<T>(T obj, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting.");

        /// <summary>
        /// Deletes the objects as <see cref="DeleteAsync{T}(T, CancellationToken)"/> does, in one
        /// transaction per provider: a batch spanning providers is not atomic.
        /// </summary>
        Task DeleteAsync<T>(IEnumerable<T> objs, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting a batch.");

        /// <summary>
        /// Deletes every row of <typeparamref name="T"/> matching <paramref name="predicate"/>
        /// without loading any, with their child collection rows, in one transaction. Returns
        /// the rows of <typeparamref name="T"/> affected. A predicate matching every row throws
        /// <see cref="ArgumentException"/>; one matching none returns 0 without a round trip.
        /// </summary>
        Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting by predicate.");

        /// <summary>
        /// Loads a child collection that was left out when its parent was loaded (excluded, or
        /// lazy): the rows whose parent is the collection's owner, with their own children,
        /// merged by ID with any children added since. A loaded collection is left as it is.
        /// </summary>
        Task LoadChildCollectionAsync(INucleusChildCollection collection, CancellationToken token = default) =>
            throw new NotSupportedException($"{GetType().Name} does not support loading a child collection on demand.");

        /// <summary>
        /// Subscribes to external-change notifications for <typeparamref name="T"/> on the provider
        /// that owns it. Throws <see cref="NotSupportedException"/> when that provider has none.
        /// </summary>
        Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support change notifications.");

        /// <summary>
        /// Opens a caller-owned context on the provider that owns <typeparamref name="T"/>, for
        /// work that must share one transaction across several types on that provider. The
        /// caller owns the outcome, and the manager's guarantees do not apply inside it:
        /// <list type="bullet">
        /// <item>Disposing a transactional context commits it unless <see cref="IDataContext.Rollback"/>
        /// or <see cref="IDataContext.TryRollback"/> was called. A failed statement does not roll
        /// it back by itself, so catch, roll back and rethrow.</item>
        /// <item>An object saved through it is marked saved as soon as its writes run, before the
        /// commit; after a rollback, reload it.</item>
        /// <item>No <c>[Cacheable]</c> cache is evicted.</item>
        /// </list>
        /// Prefer <see cref="SaveAsync{T}(IEnumerable{T}, CancellationToken)"/> when the objects
        /// are of one type.
        /// </summary>
        Task<IDataContext> CreateContextAsync<T>(ContextOptions? options = null, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support caller-owned contexts.");

        /// <summary>
        /// The worst status among the registered providers: <see cref="DataProviderStatus.Ok"/>
        /// only when every one of them is.
        /// </summary>
        DataProviderStatus Status =>
            throw new NotSupportedException($"{GetType().Name} does not report a status across providers.");

        /// <summary>
        /// Saves objects of any types, as <see cref="SaveAsync{T}(IEnumerable{T}, CancellationToken)"/>
        /// saves objects of one: in one transaction per provider that stores them, so a batch
        /// spanning providers is not atomic. A batch too large for ordinary statements is
        /// written by the provider's bulk engine; the caller does not choose.
        /// </summary>
        Task SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{GetType().Name} does not support saving a batch of mixed types.");

        /// <summary>
        /// Loads by primary key, or null when there is no such row. A <c>[Cacheable]</c> type
        /// answers from its cache.
        /// </summary>
        Task<T?> LoadByIDAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadByIDAsync<T>(id, null, cancellationToken);

        /// <summary>
        /// Loads by primary key as <paramref name="options"/> say: child collections left out,
        /// lazy collections and references brought in, deleted rows seen. A <c>[Cacheable]</c>
        /// type answers from its cache only when the options ask for nothing, since the cache
        /// holds whole live objects.
        /// </summary>
        Task<T?> LoadByIDAsync<T>(Guid id, LoadOptions? options, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support loading by ID.");

        /// <summary>
        /// Loads the rows <paramref name="predicate"/> matches (every row when null), each with
        /// its child collections. Never null: no match is an empty collection.
        /// </summary>
        Task<INucleusCollection<T>> LoadAsync<T>(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadAsync(predicate, null, cancellationToken);

        /// <summary>
        /// Loads the rows <paramref name="predicate"/> matches (every row when null) as
        /// <paramref name="options"/> say. Never null: no match is an empty collection.
        /// </summary>
        Task<INucleusCollection<T>> LoadAsync<T>(Expression<Func<T, bool>>? predicate, LoadOptions? options, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support loading by predicate.");

        /// <summary>
        /// One page of the rows <paramref name="predicate"/> matches (every row when null) and the
        /// unpaged total, in a single round trip.
        /// </summary>
        Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression<Func<T, bool>>? predicate = null, LoadOptions? options = null,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support paged loads.");

        /// <summary>
        /// Streams the rows <paramref name="predicate"/> matches (every row when null). The child
        /// graph is fetched first, and each row is yielded with its children attached.
        /// </summary>
        IAsyncEnumerable<T> LoadStreamAsync<T>(Expression<Func<T, bool>>? predicate = null, LoadOptions? options = null,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support streamed loads.");

        /// <summary>
        /// A read of rows rather than entities over the rows <paramref name="predicate"/> matches
        /// (every row when null): chosen columns ordered and paged in the database, or groups with
        /// counts, minimums, maximums and totals worked out there. See <see cref="RowQuery{T}"/>.
        /// </summary>
        RowQuery<T> Rows<T>(Expression<Func<T, bool>>? predicate = null) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support row reads.");
    }



    public class ObjectDataManager : IObjectDataManager, INucleusRuntime, ILinkedObjectLoader
    {
        private readonly IDataProviderRegistry _registry;
        private readonly IObjectSchemaProvider _objectSchema;
        private readonly ILogger<ObjectDataManager>? _logger;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly NucleusOptions _options;
        private readonly ConcurrentDictionary<Type, ILogger?> _auditLoggers = [];

        public ObjectDataManager(IDataProviderRegistry registry, IObjectSchemaProvider objectSchema,
            ILogger<ObjectDataManager>? logger = null, ILoggerFactory? loggerFactory = null, NucleusOptions? options = null,
            NucleusCache? cache = null)
        {
            _registry = registry;
            _objectSchema = objectSchema;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _options = options ?? new NucleusOptions();
            Cache = cache ?? new NucleusCache();
        }

        /// <summary>The layered cache backing [Cacheable] types (whole collection + Guid index).</summary>
        public NucleusCache Cache { get; }

        /// <summary>
        /// Conservative post-commit eviction: the type itself, its linked types, and its child
        /// collection types at every depth, since a delete now cascades through all of them.
        /// </summary>
        private void EvictCache(Type entityType) => EvictCache(entityType, []);

        private void EvictCache(Type entityType, HashSet<Type> evicted)
        {
            if (!evicted.Add(entityType))
                return;
            Cache.Evict(entityType);
            if (_objectSchema.Get(entityType) is { } schema)
            {
                foreach (var linked in schema.LinkedObjectSchemas ?? [])
                    Cache.Evict(linked.Type);
                foreach (var childProperty in schema.NucleusChildCollectionProperties ?? [])
                    EvictCache(childProperty.Type.GetGenericArguments()[0], evicted);
            }
        }

        /// <summary>Single-provider convenience: wraps the provider in a one-entry registry.</summary>
        public ObjectDataManager(IDataProvider dataProvider, IObjectSchemaProvider objectSchema, ILogger<ObjectDataManager>? logger = null)
            : this(new DataProviderRegistry([dataProvider]), objectSchema, logger)
        {
        }

        /// <summary>
        /// The audit logger for an entity type (category = the entity's full name), writing at the
        /// configured AuditLogLevel. Null when audit logging is off or no logger factory is wired.
        /// </summary>
        private ILogger? AuditLoggerFor(Type entityType)
        {
            if (!_options.EnableAuditLogging || _loggerFactory == null)
                return _logger;
            return _auditLoggers.GetOrAdd(entityType, t =>
                new LeveledLogger(_loggerFactory.CreateLogger(t.FullName ?? t.Name), _options.AuditLogLevel));
        }

        /// <summary>Re-levels every write to the configured audit level, preserving errors.</summary>
        private sealed class LeveledLogger(ILogger inner, LogLevel level) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel >= LogLevel.Error ? logLevel : level);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                inner.Log(logLevel >= LogLevel.Error ? logLevel : level, eventId, state, exception, formatter);
        }

        /// <summary>The provider owning <typeparamref name="T"/> (nearest registered root).</summary>
        private IDataProvider ProviderFor<T>() => _registry.GetProvider(typeof(T));

        /// <inheritdoc cref="IObjectDataManager.CreateContextAsync{T}(ContextOptions?, CancellationToken)"/>
        public Task<IDataContext> CreateContextAsync<T>(ContextOptions? options = null, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            ProviderFor<T>().CreateContextAsync(options, token);

        /// <inheritdoc cref="IObjectDataManager.SubscribeToChangesAsync{T}(CancellationToken)"/>
        public Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new() =>
            ProviderFor<T>().SubscribeToChangesAsync<T>(token);

        public IDataProviderRegistry Providers => _registry;
        /// <summary>The status of the unrooted default provider only. <see cref="Status"/> covers every provider.</summary>
        [Obsolete("DataProviderStatus reports the unrooted default provider only. Use Status, which reports the worst status across every provider.")]
        public DataProviderStatus DataProviderStatus => DefaultProvider.Status;

        /// <inheritdoc cref="IObjectDataManager.Status"/>
        public DataProviderStatus Status
        {
            get
            {
                var worst = DataProviderStatus.Ok;
                foreach (var provider in _registry.AllProviders)
                {
                    if (provider.Status != DataProviderStatus.Ok)
                        worst = provider.Status;
                }
                return worst;
            }
        }

        /// <summary>
        /// The unrooted default provider. It is not the provider of a type registered under a
        /// rooted provider, so reaching through it misroutes that type, and anything written
        /// through it skips the manager's cache eviction.
        /// </summary>
        [Obsolete("DataProvider is the unrooted default provider and misroutes types registered under a rooted provider. " +
            "Use the routed members instead: CreateContextAsync<T>, SubscribeToChangesAsync<T>, Rows<T>, SaveAsync and DeleteAsync.")]
        public IDataProvider DataProvider => DefaultProvider;

        private IDataProvider DefaultProvider =>
            _registry.AllProviders.FirstOrDefault(p => p.RootType == null) ?? _registry.AllProviders[0];
        public IObjectSchemaProvider ObjectSchema => _objectSchema;

        /// <summary>Initializes every registered provider; returns the worst status observed.</summary>
        public async Task<DataProviderStatus> InitializeDataProviderAsync()
        {
            var worst = DataProviderStatus.Ok;
            foreach (var provider in _registry.AllProviders)
            {
                var status = await provider.InitializeAsync();
                if (status != DataProviderStatus.Ok)
                    worst = status;
            }
            return worst;
        }

        public IObjectDataAdaptor<T> Get<T>() where T : ICoreObject<T>, new()
        {
            return new ObjectDataAdaptor<T>(this);
        }

        /// <inheritdoc cref="IObjectDataManager.Rows{T}(Expression{Func{T, bool}}?)"/>
        /// <remarks>Reads through the provider registered for <typeparamref name="T"/>.</remarks>
        public RowQuery<T> Rows<T>(Expression<Func<T, bool>>? predicate = null) where T : ICoreObject<T>, new() =>
            new(ProviderFor<T>(), predicate);

        public INucleusCollection<T>? Save<T>(T obj) where T : ICoreObject<T>, new() => Save<T>([obj]);

        /// <summary>
        /// Saves the objects in one transaction, as <see cref="SaveAsync{T}(IEnumerable{T}, CancellationToken)"/>
        /// does. Each object is validated first, and a rule that fails is thrown as it is. A
        /// failure after that rolls back; the failures <see cref="PassesThrough"/> names are thrown
        /// as they are, any other is wrapped. The objects are marked saved once the transaction
        /// has committed.
        /// </summary>
        public INucleusCollection<T>? Save<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(objs);
            var list = objs as IList<T> ?? objs.ToList();
            SaveChecks.Run(list);
            try
            {
                var provider = ProviderFor<T>();
                var linkTypes = LinkTypes(list);
                var queries = provider.CreateSaveQueries(list);
                if (queries.Count == 0)
                    return null;
                // Disposing the context commits it, so the objects are marked saved only after.
                using (var context = provider.CreateContext(ContextOptions.Transactional))
                {
                    try
                    {
                        foreach (var query in queries)
                            context.ExecuteQuery(query);
                    }
                    catch
                    {
                        context.TryRollback();
                        throw;
                    }
                }
                MarkSaved(list, [typeof(T), .. linkTypes]);
                return new NucleusCollection<T>(list);
            }
            catch (Exception ex) when (!PassesThrough(ex))
            {
                throw new Exception("Error saving objects", ex);
            }
        }

        /// <summary>
        /// Marks the objects saved, auditing each, and evicts the cached collections of the
        /// types the save wrote. Runs only once the save's transaction has committed, so a commit
        /// that fails leaves the objects with their pending changes and removals for a retry.
        /// </summary>
        private void MarkSaved<T>(IEnumerable<T> objs, IEnumerable<Type> writtenTypes) where T : ICoreObject
        {
            foreach (var obj in objs)
                obj.CommitChanges(AuditLoggerFor(obj.GetType()));
            foreach (var type in writtenTypes)
                EvictCache(type);
        }

        /// <summary>
        /// The failures a save throws as they are, as its context does: a unique violation, a
        /// cancellation, and the provider's and the context's own exceptions. A save wraps any other
        /// in "Error saving objects".
        /// </summary>
        private static bool PassesThrough(Exception ex) =>
            ex is UniqueViolationException or OperationCanceledException or DataProviderException or DataContextException;

        /// <summary>
        /// The types of the forceSave links and linked collection members a save writes or deletes,
        /// read before the save commits them, so their cached collections are evicted with the
        /// saved type's.
        /// </summary>
        private static Type[] LinkTypes<T>(IEnumerable<T> objs) where T : ICoreObject
        {
            List<ICoreObject> reaching = [.. objs.Cast<ICoreObject>().Where(o => o != null && SaveGraph.CanReachDependents(o))];
            if (reaching.Count == 0)
                return [];
            var graph = SaveGraph.Walk(reaching);
            return [.. graph.Links.Concat(graph.Members).Concat(graph.RemovedMembers).Select(o => o.GetType()).Distinct()];
        }

        public Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new() => SaveAsync<T>(obj, CancellationToken.None);

        /// <inheritdoc cref="SaveAsync{T}(IEnumerable{T}, CancellationToken)"/>
        public Task SaveAsync<T>(T obj, CancellationToken token) where T : ICoreObject<T>, new() => SaveAsync<T>([obj], token);

        public Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new() => SaveAsync<T>(objs, CancellationToken.None);

        /// <summary>
        /// Saves the objects in one transaction, with their child collections (deleting the
        /// stored children removed from them) and the forceSave links they reach. Each object is
        /// validated first (<c>Validate()</c>, then <c>PreSave()</c>), and a rule that fails is
        /// thrown as it is. A failure after that rolls back, keeping the pending changes and
        /// removals for a retry. A <see cref="UniqueViolationException"/>, an
        /// <see cref="OperationCanceledException"/>, a <see cref="DataProviderException"/> and a
        /// <see cref="DataContextException"/> are thrown as they are; any other failure is wrapped.
        /// The objects are marked saved once the transaction has committed.
        /// </summary>
        public async Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs, CancellationToken token) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(objs);
            var list = objs as IList<T> ?? objs.ToList();
            SaveChecks.Run(list);
            try
            {
                var provider = ProviderFor<T>();
                var linkTypes = LinkTypes(list);
                var queries = provider.CreateSaveQueries(list);
                if (queries.Count == 0)
                    return null;

                // Disposing the context commits it, so the objects are marked saved only after.
                await using (var context = await provider.CreateContextAsync(ContextOptions.Transactional, token))
                {
                    try
                    {
                        foreach (var query in queries)
                            await context.ExecuteQueryAsync(query, token);
                    }
                    catch
                    {
                        context.TryRollback();
                        throw;
                    }
                }
                MarkSaved(list, [typeof(T), .. linkTypes]);
                return new NucleusCollection<T>(list);
            }
            catch (Exception ex) when (!PassesThrough(ex))
            {
                throw new Exception("Error saving objects", ex);
            }
        }


        #region Delete

        public void Delete<T>(T obj) where T : ICoreObject<T>, new() => Delete<T>([obj]);

        public void Delete<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            foreach (var (provider, queries, types) in BuildDeleteQueriesByProvider(objs))
            {
                using var context = provider.CreateContext(ContextOptions.Transactional);
                try
                {
                    context.ExecuteQueries(queries);
                }
                catch
                {
                    context.TryRollback();
                    throw;
                }
                foreach (var type in types)
                    EvictCache(type);
            }
        }

        public Task DeleteAsync<T>(T obj, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            DeleteAsync<T>([obj], cancellationToken);

        public async Task DeleteAsync<T>(IEnumerable<T> objs, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            foreach (var (provider, queries, types) in BuildDeleteQueriesByProvider(objs))
            {
                using var context = await provider.CreateContextAsync(ContextOptions.Transactional, cancellationToken);
                try
                {
                    await context.ExecuteQueriesAsync(queries, cancellationToken);
                }
                catch
                {
                    context.TryRollback();
                    throw;
                }
                foreach (var type in types)
                    EvictCache(type);
            }
        }

        /// <summary>
        /// Validates every object is deletable, then builds one delete query per entity type,
        /// grouped by owning provider. The provider's query removes each object's child
        /// collection rows by parent ID, loaded or not, and throws while being built if a child
        /// type cannot be deleted, so all guards run before anything executes. Each provider
        /// group runs in its own transaction; cross-provider deletes are not atomic.
        /// </summary>
        private List<(IDataProvider Provider, IQuery[] Queries, Type[] Types)> BuildDeleteQueriesByProvider<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            var groups = GroupDeleteRoots(objs?.Where(o => o != null).Cast<ICoreObject>() ?? []);
            return [.. groups
                .GroupBy(g => _registry.GetProvider(g.Key))
                .Select(providerGroup => (providerGroup.Key,
                    providerGroup.Select(g => CreateDeleteQueryForType(providerGroup.Key, g.Key, g.Value)).OfType<IQuery>().ToArray(),
                    providerGroup.Select(g => g.Key).ToArray()))
                .Where(x => x.Item2.Length > 0)];
        }

        /// <summary>
        /// Groups the objects to delete by type. Throws before execution when a type is not
        /// [Deletable] or an object is read only; new objects are skipped, having no row.
        /// Children are not collected here: the delete removes them in SQL.
        /// </summary>
        private static Dictionary<Type, List<ICoreObject>> GroupDeleteRoots(IEnumerable<ICoreObject> roots)
        {
            var groups = new Dictionary<Type, List<ICoreObject>>();
            var seen = new HashSet<Guid>();
            foreach (var obj in roots)
            {
                if (obj.IsNew || !seen.Add(obj.ID))
                    continue;
                var schema = obj.ObjectSchema;
                if (!schema.IsDeletable)
                    throw new NotSupportedException($"Type {schema.FullName} is not [Deletable]; deletion is opt-in.");
                if (obj.IsReadOnly)
                    throw new NotSupportedException($"Cannot delete a read-only {schema.Name} ({obj.ID}).");
                if (!groups.TryGetValue(schema.Type, out var list))
                    groups[schema.Type] = list = [];
                list.Add(obj);
            }
            return groups;
        }

        /// <summary>
        /// Deletes every row of <typeparamref name="T"/> matching <paramref name="predicate"/>
        /// without loading any, with their child collection rows, in one transaction. Returns
        /// the rows of <typeparamref name="T"/> affected. A predicate matching every row throws
        /// <see cref="ArgumentException"/>; one matching none returns 0 without a round trip.
        /// </summary>
        public int DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            using var context = ProviderFor<T>().CreateContext(ContextOptions.Transactional);
            int affected;
            try
            {
                affected = context.DeleteWhere(predicate);
            }
            catch
            {
                context.TryRollback();
                throw;
            }
            EvictCache(typeof(T));
            return affected;
        }

        /// <inheritdoc cref="DeleteWhere{T}(Expression{Func{T, bool}})"/>
        public async Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            await using var context = await ProviderFor<T>().CreateContextAsync(ContextOptions.Transactional, cancellationToken);
            int affected;
            try
            {
                affected = await context.DeleteWhereAsync(predicate, cancellationToken);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken);
                throw;
            }
            EvictCache(typeof(T));
            return affected;
        }

        private static readonly ConcurrentDictionary<Type, MethodInfo> _deleteQueryFactories = [];

        private static IQuery? CreateDeleteQueryForType(IDataProvider provider, Type entityType, List<ICoreObject> objs)
        {
            var factory = _deleteQueryFactories.GetOrAdd(entityType, static t =>
                typeof(IDataProvider).GetMethod(nameof(IDataProvider.CreateDeleteQuery))!.MakeGenericMethod(t));
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entityType))!;
            foreach (var obj in objs)
                typedList.Add(obj);
            return (IQuery?)factory.Invoke(provider, [typedList]);
        }

        #endregion

        /// <summary>Loads every row; [Cacheable] types serve the shared cached collection.</summary>
        public NucleusCollection<T> Load<T>(bool forceRefresh = false) where T : ICoreObject<T>, new()
        {
            var schema = _objectSchema.Get<T>();
            if (schema is { IsCacheable: true })
                return Cache.GetOrLoad(schema, LoadAllFromProvider<T>, forceRefresh);
            return LoadAllFromProvider<T>();
        }

        private NucleusCollection<T> LoadAllFromProvider<T>() where T : ICoreObject<T>, new() =>
            LoadCore<T>(Expression.Constant(true), [], [], false);

        /// <summary>
        /// One page of rows plus the unpaged total count, fetched in a single round trip.
        /// The optional expression is a predicate, a LINQ Where tree, or null for all rows.
        /// </summary>
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression = null, CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new() =>
            LoadPagedCoreAsync<T>(skip, take, expression, [], [], false, cancellationToken);

        /// <summary>
        /// One page of rows plus the unpaged total count, in a single round trip, leaving the
        /// named child collection properties unloaded.
        /// </summary>
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression,
            IReadOnlyCollection<string> excludedCollections, CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new() =>
            LoadPagedCoreAsync<T>(skip, take, expression, excludedCollections, [], false, cancellationToken);

        /// <summary>
        /// One page of rows plus the unpaged total count, in a single round trip, leaving the
        /// named child collections unloaded and loading the named lazy ones with the page.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression,
            IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections,
            CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new() =>
            LoadPagedCoreAsync<T>(skip, take, expression, excludedCollections, includedCollections, false, cancellationToken);

        /// <inheritdoc cref="IObjectDataManager.LoadPagedAsync{T}(int, int, Expression{Func{T, bool}}?, LoadOptions?, CancellationToken)"/>
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression<Func<T, bool>>? predicate, LoadOptions? options = null,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return LoadPagedCoreAsync<T>(skip, take, predicate, options.Exclude, options.Include, options.IncludeDeleted, cancellationToken);
        }

        internal async Task<PagedResult<T>> LoadPagedCoreAsync<T>(int skip, int take, Expression? expression,
            IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections, bool includeDeleted,
            CancellationToken cancellationToken)
            where T : ICoreObject<T>, new()
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skip);
            ArgumentOutOfRangeException.ThrowIfNegative(take);
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (expression != null && ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new PagedResult<T>([]) { Skip = skip, Take = take, TotalCount = 0 };
            var provider = ProviderFor<T>();
            var query = ParseSelect<T>(provider, expression ?? Expression.Constant(true), excluded, included, includeDeleted);
            query.Skip = skip;
            query.Take = take;
            query.WithTotalCount = true;
            using var context = await provider.CreateContextAsync(token: cancellationToken);
            var dataSet = await context.GetDataSetAsync(query, cancellationToken);
            var rows = ChildGraphBinder.BuildCollection<T>(query, dataSet, LazyLoader, this, excluded);
            var result = new PagedResult<T>(rows)
            {
                Skip = skip,
                Take = take,
            };
            result.TotalCount = dataSet.TotalRowCount ?? result.Count;
            return result;
        }

        /// <summary>Loads by primary key; [Cacheable] types answer from the Guid index in O(1).</summary>
        public T? LoadByID<T>(Guid id) where T : ICoreObject<T>, new() => LoadByIDCore<T>(id, [], [], false);

        /// <summary>
        /// Loads by primary key, leaving the named child collections unloaded. [Cacheable] types
        /// answer from the cache only when nothing is excluded, since the cache holds whole objects.
        /// </summary>
        public T? LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            LoadByIDCore<T>(id, excludedCollections, [], false);

        /// <summary>
        /// Loads by primary key, leaving the named child collections unloaded and loading the
        /// named lazy ones with it. [Cacheable] types answer from the cache only when nothing is
        /// excluded or included, since the cache holds whole objects.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public T? LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections) where T : ICoreObject<T>, new() =>
            LoadByIDCore<T>(id, excludedCollections, includedCollections, false);

        /// <summary>The synchronous form of <see cref="LoadByIDAsync{T}(Guid, LoadOptions?, CancellationToken)"/>.</summary>
        public T? LoadByID<T>(Guid id, LoadOptions? options) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return LoadByIDCore<T>(id, options.Exclude, options.Include, options.IncludeDeleted);
        }

        internal T? LoadByIDCore<T>(Guid id, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections, bool includeDeleted) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(excludedCollections);
            ArgumentNullException.ThrowIfNull(includedCollections);
            if (id == Guid.Empty)
                return default;
            if (AnswersFromCache<T>(excludedCollections, includedCollections, includeDeleted))
                return FromCache<T>(id);
            return LoadCore<T>(ByIdPredicate<T>(id), excludedCollections, includedCollections, includeDeleted).FirstOrDefault();
        }

        /// <inheritdoc cref="IObjectDataManager.LoadByIDAsync{T}(Guid, CancellationToken)"/>
        public Task<T?> LoadByIDAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadByIDCoreAsync<T>(id, [], [], false, cancellationToken);

        /// <summary>
        /// Loads by primary key on the asynchronous path, leaving the named child collections
        /// unloaded and loading the named lazy ones with it. A [Cacheable] type answers from its
        /// cache when nothing is excluded or included; a cold cache is filled on the synchronous
        /// path once, as <c>Load&lt;T&gt;()</c> fills it, since the cache has no asynchronous fill.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public Task<T?> LoadByIDAsync<T>(Guid id, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadByIDCoreAsync<T>(id, excludedCollections, includedCollections, false, cancellationToken);

        /// <inheritdoc cref="IObjectDataManager.LoadByIDAsync{T}(Guid, LoadOptions?, CancellationToken)"/>
        /// <remarks>
        /// A cold cache is filled on the synchronous path once, as <c>Load&lt;T&gt;()</c> fills
        /// it, since the cache has no asynchronous fill.
        /// </remarks>
        public Task<T?> LoadByIDAsync<T>(Guid id, LoadOptions? options, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return LoadByIDCoreAsync<T>(id, options.Exclude, options.Include, options.IncludeDeleted, cancellationToken);
        }

        internal async Task<T?> LoadByIDCoreAsync<T>(Guid id, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections,
            bool includeDeleted, CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(excludedCollections);
            ArgumentNullException.ThrowIfNull(includedCollections);
            if (id == Guid.Empty)
                return default;
            if (AnswersFromCache<T>(excludedCollections, includedCollections, includeDeleted))
                return FromCache<T>(id);
            return (await LoadCoreAsync<T>(ByIdPredicate<T>(id), excludedCollections, includedCollections, includeDeleted, cancellationToken)).FirstOrDefault();
        }

        /// <summary>
        /// A [Cacheable] type with nothing excluded or included and live rows only: the cache
        /// holds whole live objects.
        /// </summary>
        private bool AnswersFromCache<T>(IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections, bool includeDeleted) where T : ICoreObject<T>, new() =>
            !includeDeleted && excludedCollections.Count == 0 && includedCollections.Count == 0 && _objectSchema.Get<T>() is { IsCacheable: true };

        private T? FromCache<T>(Guid id) where T : ICoreObject<T>, new()
        {
            Load<T>(); // ensure the collection (and its index) is populated
            return Cache.TryGetById<T>(id, out var cached) ? cached : default;
        }

        /// <summary><c>x =&gt; x.ID == id</c>, as the body a load translates.</summary>
        private static Expression ByIdPredicate<T>(Guid id) where T : ICoreObject<T>, new() =>
            Expression.Equal(
                Expression.Property(Expression.Parameter(typeof(T), "x"), nameof(ICoreObject.ID)),
                Expression.Constant(id));

        /// <summary>
        /// The schemas of the child collection properties a load leaves out. Throws for a name
        /// that is not a child collection of <typeparamref name="T"/>, so a typo cannot silently
        /// load everything.
        /// </summary>
        private IPropertySchema[] ExcludedCollections<T>(IReadOnlyCollection<string> excludedCollections)
        {
            ArgumentNullException.ThrowIfNull(excludedCollections);
            if (excludedCollections.Count == 0)
                return [];
            var properties = _objectSchema.Get<T>()?.NucleusChildCollectionProperties ?? [];
            return [.. excludedCollections.Select(name => properties.FirstOrDefault(p => p.Name == name)
                ?? throw new ArgumentException($"{typeof(T).Name}.{name} is not a child collection.", nameof(excludedCollections)))];
        }

        /// <summary>
        /// The schemas of what a load brings in with its rows: lazy child collections, and the
        /// entities of <c>[LinkedObject]</c> properties. Throws for a name that is neither on
        /// <typeparamref name="T"/>, or a collection also excluded.
        /// </summary>
        private IPropertySchema[] IncludedCollections<T>(IReadOnlyCollection<string> includedCollections, IPropertySchema[] excluded)
        {
            ArgumentNullException.ThrowIfNull(includedCollections);
            if (includedCollections.Count == 0)
                return [];
            var schema = _objectSchema.Get<T>();
            var properties = schema?.NucleusChildCollectionProperties ?? [];
            var references = schema?.LinkedObjectProperties ?? [];
            var linked = schema?.LinkedCollectionProperties ?? [];
            var included = includedCollections.Select(name => properties.FirstOrDefault(p => p.Name == name)
                ?? references.FirstOrDefault(p => p.Name == name)
                ?? linked.FirstOrDefault(p => p.Name == name)
                ?? throw new ArgumentException($"{typeof(T).Name}.{name} is not a child collection, a linked collection or a [LinkedObject] property.", nameof(includedCollections))).ToArray();
            if (included.FirstOrDefault(p => excluded.Any(e => e.Name == p.Name)) is { } both)
                throw new ArgumentException($"{typeof(T).Name}.{both.Name} is both excluded and included.", nameof(includedCollections));
            return included;
        }

        /// <summary>
        /// The select for a load: without the child queries of excluded collections, with those
        /// of included lazy ones, which a plain select leaves out, and seeing soft-deleted rows
        /// when asked to.
        /// </summary>
        private static ISelectQuery ParseSelect<T>(IDataProvider provider, Expression expression, IPropertySchema[] excluded, IPropertySchema[] included, bool includeDeleted)
        {
            var query = ParseSelectCore<T>(provider, expression, excluded);
            foreach (var property in included)
            {
                if (property.IsLinkedObject)
                    query.IncludeLinkedObject(property.Name);
                else if (property.IsLinkedCollection)
                    query.IncludeLinkedCollection(property.Name);
                else
                    query.IncludeChildCollection(property.Name);
            }
            if (includeDeleted)
                query.IncludeDeleted = true;
            return query;
        }

        private static ISelectQuery ParseSelectCore<T>(IDataProvider provider, Expression expression, IPropertySchema[] excluded)
        {
            var query = provider.ParseSelectExpression<T>(expression);
            foreach (var property in excluded)
                query.ExcludeChildCollection(property.Name);
            return query;
        }

        #region INucleusRuntime

        private static readonly ConcurrentDictionary<(string Method, Type Type, int Parameters), MethodInfo> _runtimeDispatch = [];

        /// <summary>
        /// Resolves the single-object generic overload of a manager method for a runtime entity
        /// type: the one taking the object first and <paramref name="parameters"/> parameters in all.
        /// </summary>
        private static MethodInfo SingleObjectMethod(string methodName, Type entityType, int parameters) =>
            _runtimeDispatch.GetOrAdd((methodName, entityType, parameters), static key =>
                typeof(ObjectDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == key.Method && m.IsGenericMethodDefinition &&
                        m.GetParameters().Length == key.Parameters && m.GetParameters()[0].ParameterType.IsGenericParameter)
                    .MakeGenericMethod(key.Type));

        /// <summary>
        /// Calls the single-object overload of <paramref name="methodName"/> for the object's
        /// runtime type. What it throws reaches the caller as it was thrown, not wrapped in a
        /// <see cref="TargetInvocationException"/>, so a failed rule or a refused delete reads
        /// the same through the static API as through the manager.
        /// </summary>
        private object? InvokeForType(string methodName, ICoreObject obj, object?[] args) =>
            SingleObjectMethod(methodName, obj.GetType(), args.Length)
                .Invoke(this, BindingFlags.DoNotWrapExceptions, binder: null, args, culture: null);

        void INucleusRuntime.Save(ICoreObject obj) => InvokeForType(nameof(Save), obj, [obj]);

        Task INucleusRuntime.SaveAsync(ICoreObject obj, CancellationToken token) =>
            (Task)InvokeForType(nameof(SaveAsync), obj, [obj, token])!;

        void INucleusRuntime.Delete(ICoreObject obj) => InvokeForType(nameof(Delete), obj, [obj]);

        Task INucleusRuntime.DeleteAsync(ICoreObject obj, CancellationToken token) =>
            (Task)InvokeForType(nameof(DeleteAsync), obj, [obj, token])!;

        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public void BulkSave(IEnumerable<ICoreObject?> objs) => Save(objs);

        /// <summary>
        /// Saves objects of any types: each is routed to the provider that stores it, and each
        /// provider's objects are saved in one transaction, so a batch spanning providers is not
        /// atomic. Nulls are skipped. Statements over the provider's parameter limit run as
        /// several commands; the bulk engine takes them on the asynchronous path only.
        /// </summary>
        public void Save(IEnumerable<ICoreObject?> objs)
        {
            ArgumentNullException.ThrowIfNull(objs);
            foreach (var providerGroup in objs.OfType<ICoreObject>().GroupBy(o => _registry.GetProvider(o.GetType())))
            {
                var list = providerGroup.ToList();
                // The context validates the objects before it writes, and disposing it commits,
                // so the objects are marked saved only after.
                using (var context = providerGroup.Key.CreateContext(ContextOptions.Transactional))
                {
                    try
                    {
                        context.Save(list);
                    }
                    catch
                    {
                        context.TryRollback();
                        throw;
                    }
                }
                MarkSaved(list, list.Select(o => o.GetType()).Distinct());
            }
        }

        /// <inheritdoc cref="IObjectDataManager.SaveAsync(IEnumerable{ICoreObject}, CancellationToken)"/>
        public async Task SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(objs);
            foreach (var providerGroup in objs.OfType<ICoreObject>().GroupBy(o => _registry.GetProvider(o.GetType())))
            {
                var list = providerGroup.ToList();
                await using (var context = await providerGroup.Key.CreateContextAsync(ContextOptions.Transactional, cancellationToken))
                {
                    try
                    {
                        await context.SaveAsync(list, cancellationToken);
                    }
                    catch
                    {
                        await context.TryRollbackAsync(cancellationToken);
                        throw;
                    }
                }
                MarkSaved(list, list.Select(o => o.GetType()).Distinct());
            }
        }

        /// <summary>
        /// Loads a child collection that was left out when its parent was loaded (excluded, or
        /// lazy): the rows whose parent is the collection's owner, with their own children,
        /// attached as loaded and merged by ID with any children added since. A loaded
        /// collection is left as it is.
        /// </summary>
        public Task LoadChildCollectionAsync(INucleusChildCollection collection, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(collection);
            return ChildCollectionLoader.LoadAsync(_registry.GetProvider(collection.ChildType), collection, this, token);
        }

        /// <summary>
        /// The synchronous form of <see cref="LoadChildCollectionAsync"/>, which a lazy
        /// ([RetrieveOnDemand]) collection this manager loaded calls the first time it is read.
        /// It reads on the synchronous path, not by blocking on the asynchronous one.
        /// </summary>
        public void LoadChildCollection(INucleusChildCollection collection)
        {
            ArgumentNullException.ThrowIfNull(collection);
            ChildCollectionLoader.Load(_registry.GetProvider(collection.ChildType), collection, this);
        }

        private static readonly ConcurrentDictionary<Type, MethodInfo> _linkedObjectLoads = [];

        /// <summary>
        /// Loads the entity a <c>[LinkedObject]</c> of an object this manager loaded refers to, the
        /// first time the property is read: <see cref="LoadByID{T}(Guid)"/> for its type, routed to
        /// the provider that stores it, and answered from the cache for a [Cacheable] type.
        /// </summary>
        ICoreObject? ILinkedObjectLoader.LoadLinkedObject(Type type, Guid id)
        {
            ArgumentNullException.ThrowIfNull(type);
            var load = _linkedObjectLoads.GetOrAdd(type, static t =>
                typeof(ObjectDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == nameof(LoadByID) && m.IsGenericMethodDefinition &&
                        m.GetParameters() is [{ ParameterType: var p }] && p == typeof(Guid))
                    .MakeGenericMethod(t));
            return (ICoreObject?)load.Invoke(this, BindingFlags.DoNotWrapExceptions, binder: null, [id], culture: null);
        }

        /// <summary>
        /// Loads the members of a linked collection of an object this manager loaded, the first time
        /// one is read, from the provider that stores the many type.
        /// </summary>
        void ILinkedObjectLoader.LoadLinkedCollection(INucleusLinkedCollection collection)
        {
            ArgumentNullException.ThrowIfNull(collection);
            LinkedCollectionLoader.Load(_registry.GetProvider(collection.MemberType), collection, this);
        }

        /// <inheritdoc cref="ILinkedObjectLoader.LoadLinkedCollection"/>
        Task ILinkedObjectLoader.LoadLinkedCollectionAsync(INucleusLinkedCollection collection, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(collection);
            return LinkedCollectionLoader.LoadAsync(_registry.GetProvider(collection.MemberType), collection, this, cancellationToken);
        }

        private Action<INucleusChildCollection>? _lazyLoader;

        /// <summary>
        /// <see cref="LoadChildCollection"/> as the loader every lazy collection this manager loads
        /// is handed. One delegate per manager: passing the method group in a load's row loop
        /// allocated a delegate per row.
        /// </summary>
        private Action<INucleusChildCollection> LazyLoader => _lazyLoader ??= LoadChildCollection;

        NucleusCollection<T> INucleusRuntime.LoadAll<T>() => Load<T>();

        T? INucleusRuntime.LoadByID<T>(Guid id) where T : default => LoadByID<T>(id);

        T? INucleusRuntime.LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections) where T : default =>
            LoadByID<T>(id, excludedCollections);

        NucleusCollection<T> INucleusRuntime.LoadWhere<T>(Expression expression, IReadOnlyCollection<string> excludedCollections) =>
            LoadCore<T>(expression, excludedCollections, [], false);

        Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, CancellationToken token) =>
            LoadCoreAsync<T>(expression, excludedCollections, [], false, token);

        NucleusCollection<T> INucleusRuntime.LoadWhere<T>(Expression expression) =>
            LoadCore<T>(expression, [], [], false);

        Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression expression, CancellationToken token) =>
            LoadCoreAsync<T>(expression, [], [], false, token);

        NucleusCollection<T> INucleusRuntime.LoadWhere<T>(Expression<Func<T, bool>> predicate, LoadOptions? options)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            options ??= LoadOptions.Default;
            return LoadCore<T>(predicate, options.Exclude, options.Include, options.IncludeDeleted);
        }

        Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression<Func<T, bool>> predicate, LoadOptions? options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            options ??= LoadOptions.Default;
            return LoadCoreAsync<T>(predicate, options.Exclude, options.Include, options.IncludeDeleted, cancellationToken);
        }

        T? INucleusRuntime.LoadByID<T>(Guid id, LoadOptions? options) where T : default => LoadByID<T>(id, options);

        Task<NucleusCollection<T>> INucleusRuntime.LoadAllAsync<T>(CancellationToken cancellationToken)
        {
            // The cache has no asynchronous fill: a [Cacheable] type is answered as Load<T>() answers it.
            if (_objectSchema.Get<T>() is { IsCacheable: true })
                return Task.FromResult(Load<T>());
            return LoadCoreAsync<T>(Expression.Constant(true), [], [], false, cancellationToken);
        }

        T? INucleusRuntime.LoadFirst<T>(Expression<Func<T, bool>> predicate, LoadOptions? options) where T : default =>
            LoadFirst(predicate, options);

        Task<T?> INucleusRuntime.LoadFirstAsync<T>(Expression<Func<T, bool>> predicate, LoadOptions? options, CancellationToken cancellationToken) where T : default =>
            LoadFirstAsync(predicate, options, cancellationToken);

        /// <summary>
        /// The first row <paramref name="predicate"/> matches, or null when there is none. The
        /// query asks for one row, so a predicate many rows match still reads one.
        /// </summary>
        public T? LoadFirst<T>(Expression<Func<T, bool>> predicate, LoadOptions? options = null) where T : ICoreObject<T>, new()
        {
            if (FirstQuery(predicate, options, out var provider, out var excluded) is not { } query)
                return default;
            using var context = provider.CreateContext();
            return ChildGraphBinder.BuildCollection<T>(query, context.GetDataSet(query), LazyLoader, this, excluded).FirstOrDefault();
        }

        /// <inheritdoc cref="LoadFirst{T}(Expression{Func{T, bool}}, LoadOptions?)"/>
        public async Task<T?> LoadFirstAsync<T>(Expression<Func<T, bool>> predicate, LoadOptions? options = null, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            if (FirstQuery(predicate, options, out var provider, out var excluded) is not { } query)
                return default;
            using var context = await provider.CreateContextAsync(token: cancellationToken);
            return ChildGraphBinder.BuildCollection<T>(query, await context.GetDataSetAsync(query, cancellationToken), LazyLoader, this, excluded).FirstOrDefault();
        }

        /// <summary>The select for the first match, or null when the predicate can match nothing.</summary>
        private ISelectQuery? FirstQuery<T>(Expression<Func<T, bool>> predicate, LoadOptions? options, out IDataProvider provider, out IPropertySchema[] excluded)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(predicate);
            options ??= LoadOptions.Default;
            provider = ProviderFor<T>();
            excluded = ExcludedCollections<T>(options.Exclude);
            var included = IncludedCollections<T>(options.Include, excluded);
            if (ExpressionParser.Classify(predicate) == PredicateShape.MatchesNone)
                return null;
            var query = ParseSelect<T>(provider, predicate, excluded, included, options.IncludeDeleted);
            query.Skip = 0;
            query.Take = 1;
            return query;
        }

        Task<T?> INucleusRuntime.LoadByIDAsync<T>(Guid id, LoadOptions? options, CancellationToken cancellationToken) where T : default =>
            LoadByIDAsync<T>(id, options, cancellationToken);

        #endregion
        /// <summary>Loads the matching rows, each with its child collections attached.</summary>
        public INucleusCollection<T>? Load<T>(Expression expression) where T : ICoreObject<T>, new() =>
            LoadCore<T>(expression, [], [], false);

        /// <summary>
        /// Loads the matching rows with their child collections, except the named ones, which
        /// are neither queried nor transferred and come back unloaded.
        /// </summary>
        public INucleusCollection<T>? Load<T>(Expression expression, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            LoadCore<T>(expression, excludedCollections, [], false);

        /// <summary>
        /// Loads the matching rows with their child collections, except the named excluded ones,
        /// and with the named lazy ([RetrieveOnDemand]) ones, which come in the same round trip.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public INucleusCollection<T>? Load<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections) where T : ICoreObject<T>, new() =>
            LoadCore<T>(expression, excludedCollections, includedCollections, false);

        /// <summary>The synchronous form of <see cref="LoadAsync{T}(Expression{Func{T, bool}}?, LoadOptions?, CancellationToken)"/>.</summary>
        public INucleusCollection<T> Load<T>(Expression<Func<T, bool>>? predicate, LoadOptions? options = null) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return LoadCore<T>(predicate ?? (Expression)Expression.Constant(true), options.Exclude, options.Include, options.IncludeDeleted);
        }

        internal NucleusCollection<T> LoadCore<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, bool includeDeleted) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return [];
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included, includeDeleted);
            using var context = ProviderFor<T>().CreateContext();
            return ChildGraphBinder.BuildCollection<T>(qry, context.GetDataSet(qry), LazyLoader, this, excluded);
        }

        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new() =>
            await LoadCoreAsync<T>(expression, [], [], false, CancellationToken.None);

        /// <inheritdoc cref="Load{T}(Expression)"/>
        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, CancellationToken cancellationToken) where T : ICoreObject<T>, new() =>
            await LoadCoreAsync<T>(expression, [], [], false, cancellationToken);

        /// <inheritdoc cref="Load{T}(Expression, IReadOnlyCollection{string})"/>
        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, CancellationToken cancellationToken) where T : ICoreObject<T>, new() =>
            await LoadCoreAsync<T>(expression, excludedCollections, [], false, cancellationToken);

        /// <summary>
        /// Loads the matching rows with their child collections, except the named excluded ones,
        /// and with the named lazy ([RetrieveOnDemand]) ones, which come in the same round trip.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, CancellationToken cancellationToken) where T : ICoreObject<T>, new() =>
            await LoadCoreAsync<T>(expression, excludedCollections, includedCollections, false, cancellationToken);

        /// <inheritdoc cref="IObjectDataManager.LoadAsync{T}(Expression{Func{T, bool}}?, CancellationToken)"/>
        public Task<INucleusCollection<T>> LoadAsync<T>(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadAsync(predicate, null, cancellationToken);

        /// <inheritdoc cref="IObjectDataManager.LoadAsync{T}(Expression{Func{T, bool}}?, LoadOptions?, CancellationToken)"/>
        public async Task<INucleusCollection<T>> LoadAsync<T>(Expression<Func<T, bool>>? predicate, LoadOptions? options, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return await LoadCoreAsync<T>(predicate ?? (Expression)Expression.Constant(true), options.Exclude, options.Include, options.IncludeDeleted, cancellationToken);
        }

        internal async Task<NucleusCollection<T>> LoadCoreAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, bool includeDeleted, CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return [];
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included, includeDeleted);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            return ChildGraphBinder.BuildCollection<T>(qry, await context.GetDataSetAsync(qry, cancellationToken), LazyLoader, this, excluded);
        }

        /// <summary>
        /// Streams the matching rows. The child graph is fetched first, one query per child
        /// collection, and each row is yielded with its children attached.
        /// </summary>
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadStreamCoreAsync<T>(expression, [], [], false, cancellationToken);

        /// <summary>
        /// Streams the matching rows, leaving the named child collections unloaded: their
        /// queries never run.
        /// </summary>
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadStreamCoreAsync<T>(expression, excludedCollections, [], false, cancellationToken);

        /// <summary>
        /// Streams the matching rows, leaving the named child collections unloaded and fetching
        /// the named lazy ones first with the other child queries.
        /// </summary>
        [Obsolete("Two lists of names are easy to pass in place of each other. Use the overload taking LoadOptions.")]
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadStreamCoreAsync<T>(expression, excludedCollections, includedCollections, false, cancellationToken);

        /// <inheritdoc cref="IObjectDataManager.LoadStreamAsync{T}(Expression{Func{T, bool}}?, LoadOptions?, CancellationToken)"/>
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression<Func<T, bool>>? predicate = null, LoadOptions? options = null,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            options ??= LoadOptions.Default;
            return LoadStreamCoreAsync<T>(predicate ?? (Expression)Expression.Constant(true), options.Exclude, options.Include, options.IncludeDeleted, cancellationToken);
        }

        internal async IAsyncEnumerable<T> LoadStreamCoreAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, bool includeDeleted,
            [EnumeratorCancellation] CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                yield break;
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included, includeDeleted);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            var children = await ChildGraphBinder.LoadAsync(qry, context, LazyLoader, this, cancellationToken);

            await foreach (var row in context.StreamAsync(qry, cancellationToken))
            {
                T obj = new();
                for (int i = 0; i < row.ColumnNames.Length; i++)
                {
                    obj.BindValue(row.ColumnNames[i], row.Values[i]);
                }
                ChildGraphBinder.Attach(obj, children, LazyLoader);
                ChildGraphBinder.Complete(obj, excluded, this);
                yield return obj;
            }
        }
    }

}
