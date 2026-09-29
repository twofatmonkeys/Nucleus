

using System.Linq.Expressions;
using TFM.Nucleus;

namespace TFM.Nucleus.Data
{
    public interface IDataContext : IDisposable, IAsyncDisposable
    {
        public bool HasTransaction { get; }
        public bool HasDependency { get; }
        public bool HasChildren { get; }
        public IDataContext? Parent { get; }

        public DataSet GetDataSet(IQuery query);
        public Task<DataSet> GetDataSetAsync(IQuery query);
        public Task<DataSet> GetDataSetAsync(IQuery query, CancellationToken token);
        
        public void ExecuteQuery(IQuery query);
        public Task ExecuteQueryAsync(IQuery query);
        public Task ExecuteQueryAsync(IQuery query, CancellationToken token);

        public void ExecuteQueries(IQuery[] queries);
        public Task ExecuteQueriesAsync(IQuery[] queries);
        public Task ExecuteQueriesAsync(IQuery[] queries, CancellationToken token);

        public IEnumerable<T> Load<T>(Expression expression) where T : ICoreObject<T>, new();
        public Task<IEnumerable<T>> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new();

        IAsyncEnumerable<StreamingRow> StreamAsync(IQuery query, CancellationToken ct);

        public void Save(ICoreObject obj);
        public void Delete(ICoreObject obj);

        public Task SaveAsync(ICoreObject obj);
        public Task SaveAsync(ICoreObject obj, CancellationToken token);
        public Task DeleteAsync(ICoreObject obj);
        public Task DeleteAsync(ICoreObject obj, CancellationToken token);

        /// <summary>
        /// Deletes every row of <typeparamref name="T"/> matching <paramref name="predicate"/>
        /// without loading any, cascading to child collection rows by parent ID, in this context
        /// (and its transaction, if it has one). Returns the rows of <typeparamref name="T"/>
        /// affected. A predicate matching every row is refused; one matching none returns 0.
        /// The caller owns the context, so no entity cache is evicted.
        /// </summary>
        public int DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting by predicate.");

        /// <inheritdoc cref="DeleteWhere{T}(Expression{Func{T, bool}})"/>
        public Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting by predicate.");

        public void BulkSave(IEnumerable<ICoreObject?>? objects);
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects);
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token);

        public void Rollback();
        public void TryRollback();

        public Task TryRollbackAsync();
        public Task TryRollbackAsync(CancellationToken token);

    }
}
