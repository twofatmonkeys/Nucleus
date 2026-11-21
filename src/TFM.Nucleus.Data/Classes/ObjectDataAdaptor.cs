using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    public class ObjectDataAdaptor<T> : IObjectDataAdaptor<T> where T : ICoreObject<T>, new()
    {
        private readonly ObjectDataManager _odm;
        private bool disposedValue;
        private readonly NucleusCollection<T> ChangeCollection = [];

        /// <summary>The child collection properties this query leaves unloaded.</summary>
        private readonly IReadOnlyCollection<string> _excluded = [];

        /// <summary>The lazy child collection properties this query loads with its rows.</summary>
        private readonly IReadOnlyCollection<string> _included = [];

        /// <summary>True when this query also returns soft-deleted rows.</summary>
        private readonly bool _includeDeleted;

        public Expression QueryExpression { get; private set; }
        public IQueryProvider Provider => this;

        public ObjectDataAdaptor(ObjectDataManager odm)
        {
            if (typeof(ICoreObject).IsAssignableFrom(typeof(T)) == false)
                throw new ArgumentException($"Type {typeof(T).FullName} does not implement ICoreObject");
            _odm = odm;
            QueryExpression = Expression.Constant(this);
        }

        public ObjectDataAdaptor(ObjectDataManager odm, Expression? expression) : this(odm)
        {
            if (expression != null)
                QueryExpression = expression;
        }

        /// <summary>A query over <paramref name="expression"/> that leaves the named child collections unloaded.</summary>
        public ObjectDataAdaptor(ObjectDataManager odm, Expression? expression, IReadOnlyCollection<string> excludedCollections) : this(odm, expression)
        {
            ArgumentNullException.ThrowIfNull(excludedCollections);
            _excluded = excludedCollections;
        }

        /// <summary>
        /// A query over <paramref name="expression"/> that leaves the named child collections
        /// unloaded and loads the named lazy ones with its rows.
        /// </summary>
        public ObjectDataAdaptor(ObjectDataManager odm, Expression? expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections) : this(odm, expression, excludedCollections)
        {
            ArgumentNullException.ThrowIfNull(includedCollections);
            _included = includedCollections;
        }

        private ObjectDataAdaptor(ObjectDataManager odm, Expression? expression, IReadOnlyCollection<string> excludedCollections,
            IReadOnlyCollection<string> includedCollections, bool includeDeleted) : this(odm, expression, excludedCollections, includedCollections)
        {
            _includeDeleted = includeDeleted;
        }

        /// <summary>
        /// The same query also returning the rows of a soft-deletable type that are marked
        /// deleted, and those of its child collections, on every read the query runs.
        /// </summary>
        public IQueryable<T> IncludeDeleted() => new ObjectDataAdaptor<T>(_odm, QueryExpression, _excluded, _included, true);

        /// <summary>
        /// The same query with a child collection left out, as in <c>.Exclude(b =&gt; b.Files)</c>:
        /// its rows are neither queried nor transferred, and on each loaded object the collection
        /// is unloaded, so reading it throws until <c>LoadAsync()</c> fetches it.
        /// </summary>
        public IQueryable<T> Exclude(Expression<Func<T, INucleusChildCollection>> collection)
        {
            var name = ChildCollectionSelector.PropertyName(collection);
            return new ObjectDataAdaptor<T>(_odm, QueryExpression, [.. _excluded.Append(name).Distinct(StringComparer.Ordinal)], _included, _includeDeleted);
        }

        /// <summary>
        /// The same query with a lazy ([RetrieveOnDemand]) child collection loaded with the
        /// rows, as in <c>.Include(m =&gt; m.Naps)</c>: the children of every returned parent
        /// come in the same round trip, so a listing that reads them does not load each parent's
        /// collection on its own.
        /// </summary>
        public IQueryable<T> Include(Expression<Func<T, INucleusChildCollection>> collection) =>
            Including(ChildCollectionSelector.PropertyName(collection));

        /// <summary>
        /// The same query with the entities a <c>[LinkedObject]</c> property refers to loaded with
        /// the rows, as in <c>.Include(m =&gt; m.Keeper)</c>: one query for the whole read, so a
        /// listing that reads the reference on every row does not load each one on its own. The
        /// referenced type must be stored by the same provider.
        /// </summary>
        public IQueryable<T> Include(Expression<Func<T, ICoreObject?>> reference) =>
            Including(ChildCollectionSelector.PropertyName(reference));

        /// <summary>
        /// The same query with a linked collection's members loaded with the rows, as in
        /// <c>.Include(t =&gt; t.Monkeys)</c>: one query for the members of every returned row, so
        /// a listing that reads the collection on every row does not load each one on its own.
        /// </summary>
        public IQueryable<T> Include(Expression<Func<T, INucleusLinkedCollection>> collection) =>
            Including(ChildCollectionSelector.PropertyName(collection));

        private ObjectDataAdaptor<T> Including(string name) =>
            new(_odm, QueryExpression, _excluded, [.. _included.Append(name).Distinct(StringComparer.Ordinal)], _includeDeleted);

        #region IAsyncQueryProvider

        public async Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var type = typeof(TResult);

            if (type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                type.GetGenericArguments()[0] == typeof(T))
            {
                var results = await ExecuteEnumerableAsync(expression, cancellationToken);
                return (TResult)results;
            }

            throw new InvalidCastException($"Cannot cast query result to type {typeof(TResult).FullName}");
        }

        private async Task<IEnumerable<T>> ExecuteEnumerableAsync(Expression expression, CancellationToken cancellationToken = default)
        {
            return await _odm.LoadCoreAsync<T>(expression, _excluded, _included, _includeDeleted, cancellationToken);
        }

        #endregion

        #region IQueryProvider (sync)

        public T? Execute(Expression expression)
        {
            return Execute<T>(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            var type = typeof(TResult);

            if (type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                type.GetGenericArguments()[0] == typeof(T))
            {
                var results = ExecuteEnumerable(expression);
                return (TResult)results;
            }

            throw new InvalidCastException($"Cannot cast query result to type {typeof(TResult).FullName}");
        }

        private IEnumerable<T> ExecuteEnumerable(Expression expression)
        {
            return _odm.LoadCore<T>(expression, _excluded, _included, _includeDeleted);
        }

        object? IQueryProvider.Execute(Expression expression) => Execute(expression);

        #endregion

        #region IQueryable

        Type IQueryable.ElementType => typeof(T);

        Expression IQueryable.Expression => QueryExpression;

        IQueryProvider IQueryable.Provider => Provider;

        /// <summary>
        /// Filters the set, translated to a provider query like any other Queryable.Where. Present
        /// as an instance method so that .Where(...) is unambiguous on .NET 10, where this type
        /// matches both Queryable.Where and the new AsyncEnumerable.Where.
        /// </summary>
        public IQueryable<T> Where(Expression<Func<T, bool>> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return Queryable.Where(this, predicate);
        }

        public IQueryable CreateQuery(Expression expression) => CreateQuery<T>(expression);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            Type genericAdaptorType = typeof(ObjectDataAdaptor<>);
            Type constructedAdaptorType = genericAdaptorType.MakeGenericType(typeof(TElement));

            // A derived query keeps this one's exclusions, inclusions and view of deleted rows, so
            // .Exclude(..).Where(..) still excludes and .Include(..).Where(..) still includes.
            ConstructorInfo? constructor = constructedAdaptorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                [typeof(ObjectDataManager), typeof(Expression), typeof(IReadOnlyCollection<string>), typeof(IReadOnlyCollection<string>), typeof(bool)],
                modifiers: null);
            if (constructor == null)
                throw new InvalidOperationException("Could not find suitable constructor for query instance.");

            object? instance = constructor.Invoke([_odm, expression, _excluded, _included, _includeDeleted]);

            if (instance != null)
                return (instance as IQueryable<TElement>)!;
            else
                throw new InvalidOperationException("Could not create query instance.");
        }

        #endregion

        #region IAsyncEnumerable / IEnumerable

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            await foreach (var item in _odm.LoadStreamCoreAsync<T>(QueryExpression, _excluded, _included, _includeDeleted, cancellationToken))
            {
                yield return item;
            }
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return (this as IQueryable<T>).Provider.Execute<IEnumerable<T>>(QueryExpression).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<T>)this).GetEnumerator();
        }

        #endregion

        #region Change Tracking

        public T Add(T obj)
        {
            ChangeCollection.Add(obj);
            return obj;
        }

        public IEnumerable<T> AddRange(IEnumerable<T> objs)
        {
            ChangeCollection.AddRange(objs);
            return objs;
        }

        public T[] AddRange(params T[] objs)
        {
            ChangeCollection.AddRange(objs);
            return objs;
        }

        public IEnumerable<T>? Save()
        {
            if (ChangeCollection.Count == 0)
                return [];
            var result = _odm.Save(ChangeCollection);
            ChangeCollection.Clear();
            return result;
        }

        public Task<IEnumerable<T>?> SaveAsync() => SaveAsync(CancellationToken.None);

        /// <inheritdoc cref="IObjectDataAdaptor{TICoreObject}.SaveAsync(CancellationToken)"/>
        public async Task<IEnumerable<T>?> SaveAsync(CancellationToken token)
        {
            if (ChangeCollection.Count == 0)
                return [];
            var result = await _odm.SaveAsync(ChangeCollection, token);
            ChangeCollection.Clear();
            return result;
        }

        #endregion

        #region Find

        T? IObjectDataAdaptor<T>.Find(Guid id) => _odm.LoadByIDCore<T>(id, _excluded, _included, _includeDeleted);

        // It used to wrap the synchronous LoadByID in Task.FromResult, so the caller's thread waited on the database.
        Task<T?> IObjectDataAdaptor<T>.FindAsync(Guid id) => _odm.LoadByIDCoreAsync<T>(id, _excluded, _included, _includeDeleted, CancellationToken.None);

        Task<T?> IObjectDataAdaptor<T>.FindAsync(Guid id, CancellationToken token) => _odm.LoadByIDCoreAsync<T>(id, _excluded, _included, _includeDeleted, token);

        /// <summary>One page plus the unpaged total, in a single round trip.</summary>
        public Task<PagedResult<T>> ToPagedResultAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            _odm.LoadPagedCoreAsync<T>(skip, take, QueryExpression is ConstantExpression ? null : QueryExpression, _excluded, _included, _includeDeleted, cancellationToken);

        #endregion

        #region IDisposable / IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            if (!disposedValue)
            {
                disposedValue = true;
                GC.SuppressFinalize(this);
            }

            await ValueTask.CompletedTask;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
