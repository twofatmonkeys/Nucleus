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
using System.Threading.Tasks;
using TFM.Nucleus;

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

        /// <summary>Conservative post-commit eviction: the type itself plus linked and child types.</summary>
        private void EvictCache(Type entityType)
        {
            Cache.Evict(entityType);
            if (_objectSchema.Get(entityType) is { } schema)
            {
                foreach (var linked in schema.LinkedObjectSchemas ?? [])
                    Cache.Evict(linked.Type);
                foreach (var childProperty in schema.NucleusChildCollectionProperties ?? [])
                    Cache.Evict(childProperty.Type.GetGenericArguments()[0]);
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

        public INucleusCollection<T>? Save<T>(T obj) where T : ICoreObject<T>, new() => Save<T>([obj]);

        public INucleusCollection<T>? Save<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            try
            {
                var provider = ProviderFor<T>();
                var qry = provider.CreateInsertAndUpdateQuery<T>(objs);
                if (qry == null)
                    return null;
                using var context = provider.CreateContext(ContextOptions.Transactional);
                try
                {
                    context.ExecuteQuery(qry!);
                    for(int i = 0; i < objs.Count(); i++)
                    {
                        var obj = objs.ElementAt(i);
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                    }
                    EvictCache(typeof(T));
                    return new NucleusCollection<T>(objs);
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

        public async Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new()
        {
            try
            {
                var provider = ProviderFor<T>();
                obj.DataProvider = provider;
                var qry = provider.CreateInsertAndUpdateQuery([obj]);
                if (qry == null)
                    return;
                var s = qry.FlattenLinkedQueries();
                using (var context = await provider.CreateContextAsync(ContextOptions.Transactional))
                {
                    try
                    {
                        await context.ExecuteQueryAsync(qry!);
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                        EvictCache(typeof(T));
                        return;
                    }
                    catch
                    {
                        context.TryRollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error saving objects", ex);
            }
        }


        public async Task<INucleusCollection<T>?> SaveAsync<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            try
            {
                var provider = ProviderFor<T>();
                var list = objs as IList<T> ?? objs.ToList();
                foreach (var obj in list)
                    obj.DataProvider = provider;

                var qry = provider.CreateInsertAndUpdateQuery(list);
                if (qry == null)
                    return null;
                
                using var context = await provider.CreateContextAsync(ContextOptions.Transactional);
                try
                {
                    await context.ExecuteQueryAsync(qry!);
                    foreach (var obj in list)
                    {
                        obj.CommitChanges(AuditLoggerFor(obj.GetType()));
                    }

                    EvictCache(typeof(T));
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
        /// Validates every object (and cascaded child) is deletable, then builds one delete query
        /// per entity type, grouped by owning provider. All guards run before anything executes.
        /// Each provider group runs in its own transaction — cross-provider deletes are not atomic.
        /// </summary>
        private List<(IDataProvider Provider, IQuery[] Queries, Type[] Types)> BuildDeleteQueriesByProvider<T>(IEnumerable<T> objs) where T : ICoreObject<T>, new()
        {
            var groups = ExpandDeleteGraph(objs?.Where(o => o != null).Cast<ICoreObject>() ?? []);
            return [.. groups
                .GroupBy(g => _registry.GetProvider(g.Key))
                .Select(providerGroup => (providerGroup.Key,
                    providerGroup.Select(g => CreateDeleteQueryForType(providerGroup.Key, g.Key, g.Value)).OfType<IQuery>().ToArray(),
                    providerGroup.Select(g => g.Key).ToArray()))
                .Where(x => x.Item2.Length > 0)];
        }

        /// <summary>
        /// Depth-first expansion of the delete graph: each object's loaded child-collection
        /// members are deleted with it. Throws before execution when any type in the graph is
        /// not [Deletable] or any object is read only; new objects are skipped.
        /// </summary>
        private static Dictionary<Type, List<ICoreObject>> ExpandDeleteGraph(IEnumerable<ICoreObject> roots)
        {
            var groups = new Dictionary<Type, List<ICoreObject>>();
            var seen = new HashSet<Guid>();
            var stack = new Stack<ICoreObject>(roots);
            while (stack.Count > 0)
            {
                var obj = stack.Pop();
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
                foreach (var collection in obj.NucleusChildCollections)
                    foreach (var child in collection.ChildObjects)
                        stack.Push(child);
            }
            return groups;
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
        public async Task<PagedResult<T>> LoadPagedAsync<T>(int skip, int take, Expression? expression = null, CancellationToken cancellationToken = default)
            where T : ICoreObject<T>, new()
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skip);
            ArgumentOutOfRangeException.ThrowIfNegative(take);
            var provider = ProviderFor<T>();
            var query = provider.ParseSelectExpression<T>(expression ?? Expression.Constant(true));
            query.Skip = skip;
            query.Take = take;
            query.WithTotalCount = true;
            using var context = await provider.CreateContextAsync(token: cancellationToken);
            var dataSet = await context.GetDataSetAsync(query, cancellationToken);
            var result = new PagedResult<T>(dataSet.BindRows<T>())
            {
                Skip = skip,
                Take = take,
            };
            result.TotalCount = dataSet.TotalRowCount ?? result.Count;
            return result;
        }

        /// <summary>Loads by primary key; [Cacheable] types answer from the Guid index in O(1).</summary>
        public T? LoadByID<T>(Guid id) where T : ICoreObject<T>, new()
        {
            if (id == Guid.Empty)
                return default;
            var schema = _objectSchema.Get<T>();
            if (schema is { IsCacheable: true })
            {
                Load<T>(); // ensure the collection (and its index) is populated
                return Cache.TryGetById<T>(id, out var cached) ? cached : default;
            }
            var parameter = Expression.Parameter(typeof(T), "x");
            var predicate = Expression.Equal(
                Expression.Property(parameter, nameof(ICoreObject.ID)),
                Expression.Constant(id));
            return ((NucleusCollection<T>?)Load<T>(predicate)) is { Count: > 0 } loaded ? loaded[0] : default;
        }

        #region INucleusRuntime

        private static readonly ConcurrentDictionary<(string Method, Type Type), MethodInfo> _runtimeDispatch = [];

        /// <summary>Resolves the single-object generic overload of a manager method for a runtime entity type.</summary>
        private static MethodInfo SingleObjectMethod(string methodName, Type entityType) =>
            _runtimeDispatch.GetOrAdd((methodName, entityType), static key =>
                typeof(ObjectDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == key.Method && m.IsGenericMethodDefinition &&
                        m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.IsGenericParameter)
                    .MakeGenericMethod(key.Type));

        void INucleusRuntime.Save(ICoreObject obj) =>
            SingleObjectMethod(nameof(Save), obj.GetType()).Invoke(this, [obj]);

        Task INucleusRuntime.SaveAsync(ICoreObject obj, CancellationToken token) =>
            (Task)SingleObjectMethod(nameof(SaveAsync), obj.GetType()).Invoke(this, [obj])!;

        void INucleusRuntime.Delete(ICoreObject obj) =>
            SingleObjectMethod(nameof(Delete), obj.GetType()).Invoke(this, [obj]);

        Task INucleusRuntime.DeleteAsync(ICoreObject obj, CancellationToken token) =>
            (Task)SingleObjectMethod(nameof(DeleteAsync), obj.GetType()).Invoke(this, [obj, token])!;

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

        NucleusCollection<T> INucleusRuntime.LoadAll<T>() => Load<T>();

        T? INucleusRuntime.LoadByID<T>(Guid id) where T : default => LoadByID<T>(id);

        NucleusCollection<T> INucleusRuntime.LoadWhere<T>(Expression expression) =>
            (NucleusCollection<T>?)Load<T>(expression) ?? [];

        async Task<NucleusCollection<T>> INucleusRuntime.LoadWhereAsync<T>(Expression expression, CancellationToken token) =>
            (NucleusCollection<T>?)await LoadAsync<T>(expression, token) ?? [];

        #endregion
        public INucleusCollection<T>? Load<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            var qry = ProviderFor<T>().ParseSelectExpression<T>(expression);
            using var context = ProviderFor<T>().CreateContext();
            var ds = context.GetDataSet(qry);
            var data = ds.BuildCollection<T>();
            return data;
        }

        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            return await LoadAsync<T>(expression, CancellationToken.None);
        }

        public async Task<INucleusCollection<T>?> LoadAsync<T>(Expression expression, CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            var qry = ProviderFor<T>().ParseSelectExpression<T>(expression);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            var ds = await context.GetDataSetAsync(qry, cancellationToken);
            var data = ds.BuildCollection<T>();
            return data;
        }

        public async IAsyncEnumerable<T> LoadStreamAsync<T>(Expression expression, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : ICoreObject<T>, new()
        {
            var qry = ProviderFor<T>().ParseSelectExpression<T>(expression);
            using var context = await ProviderFor<T>().CreateContextAsync(token: cancellationToken);
            // Pre-load entire child graph, keyed by parent ID at each level
            var childMaps = await PreloadChildGraphAsync(qry, qry.DataTableSchema!.ObjectSchema, context, cancellationToken);


            await foreach (var row in context.StreamAsync(qry, cancellationToken))
            {
                T obj = new();
                for (int i = 0; i < row.ColumnNames.Length; i++)
                {
                    obj.BindValue(row.ColumnNames[i], row.Values[i]);
                }
                AttachChildGraph(obj, childMaps);
                yield return obj;
            }
        }

        private async Task<ChildGraph?> PreloadChildGraphAsync(
    IQuery query,
    IObjectSchema schema,
    IDataContext context,
    CancellationToken cancellationToken)
        {
            if (schema.NucleusChildCollectionProperties is not { Length: > 0 } childProps)
                return null;
            if (query.LinkedQueries.ToList() is not { Count: > 0 })
                return null;

            var graph = new ChildGraph();

            foreach (var childQuery in query.LinkedQueries)
            {
                // Match query to property via ChildType
                var childCollectionProp = schema.NucleusChildCollectionProperties.FirstOrDefault(p => 
                    p.ChildType == childQuery.DataTableSchema?.ObjectSchema.Type
                );
                // No matching collection property on the parent means this child query does not
                // belong to this graph; skip it rather than dereferencing null further down.
                if (childCollectionProp is null) continue;
                var prop = schema.GetObjectSchema(childQuery.DataTableSchema?.ObjectSchema.Type
                    ?? throw new InvalidOperationException("Child query DataTableSchema or ObjectSchema is null"));
                if (prop is null) continue;

                var map = new Dictionary<Guid, List<ICoreObject>>();

                await foreach (var row in context.StreamAsync(childQuery, cancellationToken))
                {
                    var childObj = (ICoreObject)Activator.CreateInstance(prop.Type!)!;
                    Guid parentId = Guid.Empty;

                    for (int i = 0; i < row.ColumnNames.Length; i++)
                    {
                        if (row.ColumnNames[i] == prop.ParentObjectProperty!.Name && row.Values[i] is Guid rowParentId)
                            parentId = rowParentId;
                        childObj.BindValue(row.ColumnNames[i], row.Values[i]);
                    }

                    if (parentId == Guid.Empty) continue;

                    if (!map.TryGetValue(parentId, out var list))
                    {
                        list = [];
                        map[parentId] = list;
                    }
                    list.Add(childObj);
                }

                // Recurse for grandchildren
                var grandchildGraph = await PreloadChildGraphAsync(
                    childQuery, prop, context, cancellationToken);

                // Attach grandchildren to children
                if (grandchildGraph is not null)
                {
                    foreach (var children in map.Values)
                        foreach (var child in children)
                            AttachChildGraph(child, grandchildGraph);
                }

                graph.Add(childCollectionProp, map);
            }

            return graph;
        }

        private static void AttachChildGraph(ICoreObject parent, ChildGraph? graph)
        {
            if (graph is null) return;

            var parentId = parent.ID;

            foreach (var (prop, map) in graph)
            {
                if (!map.TryGetValue(parentId, out var children)) continue;

                // Get  the collection, populate it
                var collection = prop.Getter?.Invoke(parent, null);
                if (collection is IList list)
                {
                    foreach (var child in children)
                        list.Add(child);
                }
                //prop.SetValue(parent, collection);
            }
        }

        private class ChildGraph : List<(IPropertySchema Property, Dictionary<Guid, List<ICoreObject>> Map)>
        {
            public void Add(IPropertySchema prop, Dictionary<Guid, List<ICoreObject>> map)
                => Add((prop, map));
        }


    }

    public enum DataSchemaStatus
    {
        Current,
        OutOfDate,
        Error
    }
}
