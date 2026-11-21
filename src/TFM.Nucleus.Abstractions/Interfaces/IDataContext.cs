

using System.Linq.Expressions;
using TFM.Nucleus;

namespace TFM.Nucleus.Data
{
    public interface IDataContext : IDisposable, IAsyncDisposable
    {
        public bool HasTransaction { get; }
        /// <summary>The old name of <see cref="WatchesForChanges"/>.</summary>
        [Obsolete("Use WatchesForChanges.")]
        public bool HasDependency { get; }

        /// <summary>True when the context was opened with <see cref="ContextOptions.WatchForChanges"/>.</summary>
        public bool WatchesForChanges =>
#pragma warning disable CS0618 // an implementation that predates this member answers under the old name
            HasDependency;
#pragma warning restore CS0618
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

        /// <summary>Loads the rows <paramref name="predicate"/> matches, in this context.</summary>
        public IEnumerable<T> Load<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return Load<T>((Expression)predicate);
        }

        /// <inheritdoc cref="Load{T}(Expression{Func{T, bool}})"/>
        public Task<IEnumerable<T>> LoadAsync<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return LoadAsync<T>((Expression)predicate);
        }

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

        /// <summary>
        /// Saves objects of any types this context's provider stores, in this context (and its
        /// transaction, if it has one). Each object is validated first; nulls are skipped. A
        /// batch too large for ordinary statements is written by the provider's bulk engine on
        /// the asynchronous path; the caller does not choose.
        /// </summary>
        /// <remarks>An implementation that predates this member is served by its batch save under the old name.</remarks>
        public void Save(IEnumerable<ICoreObject?> objs) =>
#pragma warning disable CS0618
            BulkSave(objs);
#pragma warning restore CS0618

        /// <inheritdoc cref="Save(IEnumerable{ICoreObject})"/>
        public Task SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken = default) =>
#pragma warning disable CS0618
            BulkSaveAsync(objs, cancellationToken);
#pragma warning restore CS0618

        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public void BulkSave(IEnumerable<ICoreObject?>? objects);
        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects);
        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token);

        /// <summary>
        /// Commits the context's transaction. On a root context it commits at once, and throws if
        /// a failure marked the transaction for rollback, in which case it rolls back instead. On a
        /// context opened with a parent it marks this part of the work complete, and the root
        /// commits the whole. A context without a transaction has nothing to commit. Nothing may
        /// run on a context after it commits.
        /// </summary>
        /// <remarks>An implementation that predates this member does not support it.</remarks>
        public void Commit() =>
            throw new NotSupportedException($"{GetType().Name} does not support an explicit commit.");

        /// <inheritdoc cref="Commit"/>
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{GetType().Name} does not support an explicit commit.");

        public void Rollback();

        /// <summary>
        /// Rolls the context's transaction back, for a caller on the asynchronous path. A
        /// rollback sends nothing a caller waits on, so this completes at once.
        /// </summary>
        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rollback();
            return Task.CompletedTask;
        }
        public void TryRollback();

        public Task TryRollbackAsync();
        public Task TryRollbackAsync(CancellationToken token);

    }
}
