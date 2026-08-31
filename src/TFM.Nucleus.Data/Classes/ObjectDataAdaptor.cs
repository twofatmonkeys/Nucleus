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

namespace TFM.Nucleus.Data
{
    public class ObjectDataAdaptor<T> : IObjectDataAdaptor<T> where T : ICoreObject<T>, new()
    {
        private readonly ObjectDataManager _odm;
        private bool disposedValue;
        private readonly NucleusCollection<T> ChangeCollection = [];

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
            return await _odm.LoadAsync<T>(expression, cancellationToken) ?? Enumerable.Empty<T>();
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
            return _odm.Load<T>(expression) ?? Enumerable.Empty<T>();
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

            ConstructorInfo? constructor = constructedAdaptorType.GetConstructor([_odm.GetType(), typeof(Expression)]);
            if (constructor == null)
                throw new InvalidOperationException("Could not find suitable constructor for query instance.");

            object? instance = constructor.Invoke([_odm, expression]);

            if (instance != null)
                return (instance as IQueryable<TElement>)!;
            else
                throw new InvalidOperationException("Could not create query instance.");
        }

        #endregion

        #region IAsyncEnumerable / IEnumerable

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            await foreach (var item in _odm.LoadStreamAsync<T>(QueryExpression, cancellationToken))
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

        public async Task<IEnumerable<T>?> SaveAsync()
        {
            if (ChangeCollection.Count == 0)
                return [];
            var result = await _odm.SaveAsync(ChangeCollection);
            ChangeCollection.Clear();
            return result;
        }

        #endregion

        #region Find

        T? IObjectDataAdaptor<T>.Find(Guid id) => _odm.LoadByID<T>(id);

        Task<T?> IObjectDataAdaptor<T>.FindAsync(Guid id) => Task.FromResult(_odm.LoadByID<T>(id));

        /// <summary>One page plus the unpaged total, in a single round trip.</summary>
        public Task<PagedResult<T>> ToPagedResultAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            _odm.LoadPagedAsync<T>(skip, take, QueryExpression is ConstantExpression ? null : QueryExpression, cancellationToken);

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
