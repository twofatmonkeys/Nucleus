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

namespace TFM.Nucleus.Data
{
    public interface IObjectDataManager
    {
        IDataProvider DataProvider { get; }
        IObjectSchemaProvider ObjectSchema { get; }
        DataProviderStatus DataProviderStatus { get; }
        //Task<DataSchemaStatus> CheckSchemaAsync();
        Task<DataProviderStatus> InitializeDataProviderAsync();
        IObjectDataAdaptor<T> Get<T>() where T : ICoreObject<T>, new();

        Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new();

        /// <summary>
        /// A read of rows rather than entities over the rows <paramref name="predicate"/> matches
        /// (every row when null): chosen columns ordered and paged in the database, or groups with
        /// counts, minimums, maximums and totals worked out there. See <see cref="RowQuery{T}"/>.
        /// </summary>
        RowQuery<T> Rows<T>(Expression<Func<T, bool>>? predicate = null) where T : ICoreObject<T>, new() =>
            new(DataProvider, predicate);
    }



    public class ObjectDataManager : IObjectDataManager, INucleusRuntime
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

        public IDataProviderRegistry Providers => _registry;
        public DataProviderStatus DataProviderStatus => DataProvider.Status;
        public IDataProvider DataProvider =>
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

        public INucleusCollection<T>? Save<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            try
            {
                var provider = ProviderFor<T>();
                var list = objs as IList<T> ?? objs.ToList();
                var linkTypes = LinkTypes(list);
                var queries = provider.CreateSaveQueries(list);
                if (queries.Count == 0)
                    return null;
                using var context = provider.CreateContext(ContextOptions.Transactional);
                try
                {
                    foreach (var query in queries)
                        context.ExecuteQuery(query);
                    foreach (var obj in list)
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                    EvictCache(typeof(T));
                    foreach (var type in linkTypes)
                        EvictCache(type);
                    return new NucleusCollection<T>(list);
                }
                catch
                {
                    context.TryRollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error saving objects", ex);
            }
        }

        /// <summary>
        /// The types of the forceSave links a save writes, read before the save commits them, so
        /// their cached collections are evicted with the saved type's.
        /// </summary>
        private static Type[] LinkTypes<T>(IEnumerable<T> objs) where T : ICoreObject
        {
            List<ICoreObject> reaching = [.. objs.Cast<ICoreObject>().Where(o => o != null && SaveGraph.CanReachDependents(o))];
            return reaching.Count == 0 ? [] : [.. SaveGraph.Walk(reaching).Links.Select(link => link.GetType()).Distinct()];
        }

        public Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new() => SaveAsync<T>(obj, CancellationToken.None);

        /// <inheritdoc cref="SaveAsync{T}(IEnumerable{T}, CancellationToken)"/>
        public Task SaveAsync<T>(T obj, CancellationToken token) where T : ICoreObject<T>, new() => SaveAsync<T>([obj], token);

        public Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new() => SaveAsync<T>(objs, CancellationToken.None);

        /// <summary>
        /// Saves the objects in one transaction, with their child collections (deleting the
        /// stored children removed from them) and the forceSave links they reach. A failure is
        /// wrapped and rolls back, keeping the pending removals for a retry; a cancelled save
        /// rolls back and throws <see cref="OperationCanceledException"/> as it is.
        /// </summary>
        public async Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs, CancellationToken token) where T : ICoreObject<T>, new()
        {
            try
            {
                var provider = ProviderFor<T>();
                var list = objs as IList<T> ?? objs.ToList();
                var linkTypes = LinkTypes(list);
                var queries = provider.CreateSaveQueries(list);
                if (queries.Count == 0)
                    return null;

                using var context = await provider.CreateContextAsync(ContextOptions.Transactional, token);
                try
                {
                    foreach (var query in queries)
                        await context.ExecuteQueryAsync(query, token);
                    foreach (var obj in list)
                    {
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                    }

                    EvictCache(typeof(T));
                    foreach (var type in linkTypes)
                        EvictCache(type);
                    return new NucleusCollection<T>(list);
                }
                catch
                {
                    context.TryRollback();
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
            (NucleusCollection<T>?)Load<T>(Expression.Constant(true)) ?? [];

        /// <summary>
        /// One page of rows plus the unpaged total count, fetched in a single round trip.
        /// The optional expression is a LINQ Where tree (or null for all rows).
        /// </summary>
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression = null, CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new() =>
            LoadPagedAsync<T>(skip, take, expression, [], cancellationToken);

        /// <summary>
        /// One page of rows plus the unpaged total count, in a single round trip, leaving the
        /// named child collection properties unloaded.
        /// </summary>
        public Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression,
            IReadOnlyCollection<string> excludedCollections, CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new() =>
            LoadPagedAsync<T>(skip, take, expression, excludedCollections, [], cancellationToken);

        /// <summary>
        /// One page of rows plus the unpaged total count, in a single round trip, leaving the
        /// named child collections unloaded and loading the named lazy ones with the page.
        /// </summary>
        public async Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression,
            IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections,
            CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new()
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skip);
            ArgumentOutOfRangeException.ThrowIfNegative(take);
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (expression != null && ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new PagedResult<T>([]) { Skip = skip, Take = take, TotalCount = 0 };
            var provider = ProviderFor<T>();
            var query = ParseSelect<T>(provider, expression ?? Expression.Constant(true), excluded, included);
            query.Skip = skip;
            query.Take = take;
            query.WithTotalCount = true;
            using var context = await provider.CreateContextAsync(token: cancellationToken);
            var dataSet = await context.GetDataSetAsync(query, cancellationToken);
            var rows = ChildGraphBinder.BuildCollection<T>(query, dataSet, LazyLoader);
            MarkUnloaded(rows, excluded);
            var result = new PagedResult<T>(rows)
            {
                Skip = skip,
                Take = take,
            };
            result.TotalCount = dataSet.TotalRowCount ?? result.Count;
            return result;
        }

        /// <summary>Loads by primary key; [Cacheable] types answer from the Guid index in O(1).</summary>
        public T? LoadByID<T>(Guid id) where T : ICoreObject<T>, new() => LoadByID<T>(id, []);

        /// <summary>
        /// Loads by primary key, leaving the named child collections unloaded. [Cacheable] types
        /// answer from the cache only when nothing is excluded, since the cache holds whole objects.
        /// </summary>
        public T? LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            LoadByID<T>(id, excludedCollections, []);

        /// <summary>
        /// Loads by primary key, leaving the named child collections unloaded and loading the
        /// named lazy ones with it. [Cacheable] types answer from the cache only when nothing is
        /// excluded or included, since the cache holds whole objects.
        /// </summary>
        public T? LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(excludedCollections);
            ArgumentNullException.ThrowIfNull(includedCollections);
            if (id == Guid.Empty)
                return default;
            var schema = _objectSchema.Get<T>();
            if (schema is { IsCacheable: true } && excludedCollections.Count == 0 && includedCollections.Count == 0)
            {
                Load<T>(); // ensure the collection (and its index) is populated
                return Cache.TryGetById<T>(id, out var cached) ? cached : default;
            }
            var parameter = Expression.Parameter(typeof(T), "x");
            var predicate = Expression.Equal(
                Expression.Property(parameter, nameof(ICoreObject.ID)),
                Expression.Constant(id));
            return ((NucleusCollection<T>?)Load<T>(predicate, excludedCollections, includedCollections)) is { Count: > 0 } loaded ? loaded[0] : default;
        }

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
        /// The schemas of the lazy child collections a load brings in with its rows. Throws for a
        /// name that is not a child collection of <typeparamref name="T"/>, or one also excluded.
        /// </summary>
        private IPropertySchema[] IncludedCollections<T>(IReadOnlyCollection<string> includedCollections, IPropertySchema[] excluded)
        {
            ArgumentNullException.ThrowIfNull(includedCollections);
            if (includedCollections.Count == 0)
                return [];
            var properties = _objectSchema.Get<T>()?.NucleusChildCollectionProperties ?? [];
            var included = includedCollections.Select(name => properties.FirstOrDefault(p => p.Name == name)
                ?? throw new ArgumentException($"{typeof(T).Name}.{name} is not a child collection.", nameof(includedCollections))).ToArray();
            if (included.FirstOrDefault(p => excluded.Any(e => e.Name == p.Name)) is { } both)
                throw new ArgumentException($"{typeof(T).Name}.{both.Name} is both excluded and included.", nameof(includedCollections));
            return included;
        }

        /// <summary>The select for a load, without the child queries of excluded collections.</summary>
        private static ISelectQuery ParseSelect<T>(IDataProvider provider, Expression expression, IPropertySchema[] excluded) =>
            ParseSelect<T>(provider, expression, excluded, []);

        /// <summary>
        /// The select for a load: without the child queries of excluded collections, and with
        /// those of included lazy ones, which a plain select leaves out.
        /// </summary>
        private static ISelectQuery ParseSelect<T>(IDataProvider provider, Expression expression, IPropertySchema[] excluded, IPropertySchema[] included)
        {
            var query = ParseSelectCore<T>(provider, expression, excluded);
            foreach (var property in included)
                query.IncludeChildCollection(property.Name);
            return query;
        }

        private static ISelectQuery ParseSelectCore<T>(IDataProvider provider, Expression expression, IPropertySchema[] excluded)
        {
            var query = provider.ParseSelectExpression<T>(expression);
            foreach (var property in excluded)
                query.ExcludeChildCollection(property.Name);
            return query;
        }

        /// <summary>Marks each excluded collection of each loaded object as not loaded.</summary>
        private static void MarkUnloaded<T>(IEnumerable<T> objs, IPropertySchema[] excluded) where T : ICoreObject<T>
        {
            if (excluded.Length == 0)
                return;
            foreach (var obj in objs)
                foreach (var property in excluded)
                    if (property.Getter?.Invoke(obj, null) is INucleusChildCollection collection)
                        collection.MarkUnloaded();
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

        void INucleusRuntime.Save(ICoreObject obj) =>
            SingleObjectMethod(nameof(Save), obj.GetType(), 1).Invoke(this, [obj]);

        Task INucleusRuntime.SaveAsync(ICoreObject obj, CancellationToken token) =>
            (Task)SingleObjectMethod(nameof(SaveAsync), obj.GetType(), 2).Invoke(this, [obj, token])!;

        void INucleusRuntime.Delete(ICoreObject obj) =>
            SingleObjectMethod(nameof(Delete), obj.GetType(), 1).Invoke(this, [obj]);

        Task INucleusRuntime.DeleteAsync(ICoreObject obj, CancellationToken token) =>
            (Task)SingleObjectMethod(nameof(DeleteAsync), obj.GetType(), 2).Invoke(this, [obj, token])!;

        /// <summary>
        /// Routes each object to its owning provider and bulk-saves per provider in one
        /// transaction each; cross-provider batches are not atomic.
        /// </summary>
        public void BulkSave(IEnumerable<ICoreObject?> objs)
        {
            foreach (var providerGroup in objs.OfType<ICoreObject>().GroupBy(o => _registry.GetProvider(o.GetType())))
            {
                var list = providerGroup.ToList();
                using var context = providerGroup.Key.CreateContext(ContextOptions.Transactional);
                try
                {
                    context.BulkSave(list);
                    foreach (var obj in list)
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                    foreach (var type in list.Select(o => o.GetType()).Distinct())
                        EvictCache(type);
                }
                catch
                {
                    context.TryRollback();
                    throw;
                }
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
            return ChildCollectionLoader.LoadAsync(_registry.GetProvider(collection.ChildType), collection, token);
        }

        /// <summary>
        /// The synchronous form of <see cref="LoadChildCollectionAsync"/>, which a lazy
        /// ([RetrieveOnDemand]) collection this manager loaded calls the first time it is read.
        /// It reads on the synchronous path, not by blocking on the asynchronous one.
        /// </summary>
        public void LoadChildCollection(INucleusChildCollection collection)
        {
            ArgumentNullException.ThrowIfNull(collection);
            ChildCollectionLoader.Load(_registry.GetProvider(collection.ChildType), collection);
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
            (NucleusCollection<T>?)Load<T>(expression, excludedCollections) ?? [];

        async Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, CancellationToken token) =>
            (NucleusCollection<T>?)await LoadAsync<T>(expression, excludedCollections, token) ?? [];

        NucleusCollection<T> INucleusRuntime.LoadWhere<T>(Expression expression) =>
            (NucleusCollection<T>?)Load<T>(expression) ?? [];

        async Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression expression, CancellationToken token) =>
            (NucleusCollection<T>?)await LoadAsync<T>(expression, token) ?? [];

        #endregion
        /// <summary>Loads the matching rows, each with its child collections attached.</summary>
        public INucleusCollection<T>? Load<T>(Expression expression) where T : ICoreObject<T>, new() =>
            Load<T>(expression, []);

        /// <summary>
        /// Loads the matching rows with their child collections, except the named ones, which
        /// are neither queried nor transferred and come back unloaded.
        /// </summary>
        public INucleusCollection<T>? Load<T>(Expression expression, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            Load<T>(expression, excludedCollections, []);

        /// <summary>
        /// Loads the matching rows with their child collections, except the named excluded ones,
        /// and with the named lazy ([RetrieveOnDemand]) ones, which come in the same round trip.
        /// </summary>
        public INucleusCollection<T>? Load<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, IReadOnlyCollection<string> includedCollections) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included);
            using var context = ProviderFor<T>().CreateContext();
            var rows = ChildGraphBinder.BuildCollection<T>(qry, context.GetDataSet(qry), LazyLoader);
            MarkUnloaded(rows, excluded);
            return rows;
        }

        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            return await LoadAsync<T>(expression, CancellationToken.None);
        }

        /// <inheritdoc cref="Load{T}(Expression)"/>
        public Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, CancellationToken cancellationToken) where T : ICoreObject<T>, new() =>
            LoadAsync<T>(expression, [], cancellationToken);

        /// <inheritdoc cref="Load{T}(Expression, IReadOnlyCollection{string})"/>
        public Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, CancellationToken cancellationToken) where T : ICoreObject<T>, new() =>
            LoadAsync<T>(expression, excludedCollections, [], cancellationToken);

        /// <inheritdoc cref="Load{T}(Expression, IReadOnlyCollection{string}, IReadOnlyCollection{string})"/>
        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            var rows = ChildGraphBinder.BuildCollection<T>(qry, await context.GetDataSetAsync(qry, cancellationToken), LazyLoader);
            MarkUnloaded(rows, excluded);
            return rows;
        }

        /// <summary>
        /// Streams the matching rows. The child graph is fetched first, one query per child
        /// collection, and each row is yielded with its children attached.
        /// </summary>
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadStreamAsync<T>(expression, [], cancellationToken);

        /// <summary>
        /// Streams the matching rows, leaving the named child collections unloaded: their
        /// queries never run.
        /// </summary>
        public IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            LoadStreamAsync<T>(expression, excludedCollections, [], cancellationToken);

        /// <summary>
        /// Streams the matching rows, leaving the named child collections unloaded and fetching
        /// the named lazy ones first with the other child queries.
        /// </summary>
        public async IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            var excluded = ExcludedCollections<T>(excludedCollections);
            var included = IncludedCollections<T>(includedCollections, excluded);
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                yield break;
            var qry = ParseSelect<T>(ProviderFor<T>(), expression, excluded, included);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            var children = await ChildGraphBinder.LoadAsync(qry, context, LazyLoader, cancellationToken);

            await foreach (var row in context.StreamAsync(qry, cancellationToken))
            {
                T obj = new();
                for (int i = 0; i < row.ColumnNames.Length; i++)
                {
                    obj.BindValue(row.ColumnNames[i], row.Values[i]);
                }
                ChildGraphBinder.Attach(obj, children, LazyLoader);
                MarkUnloaded([obj], excluded);
                yield return obj;
            }
        }
    }

    public enum DataSchemaStatus
    {
        Current,
        OutOfDate,
        Error
    }
}
