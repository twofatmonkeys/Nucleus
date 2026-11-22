using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The <see cref="IDataContext"/> every relational provider shares, over System.Data.Common:
    /// one connection, one command and an optional transaction per context; a tree of child
    /// contexts on their root's connection and transaction, which commits or rolls back whole; a
    /// per-provider limit on held connections (<see cref="ConnectionSlots"/>); reads into <see cref="DataSet"/> with child collection
    /// result sets; deletes that report the rows they affected; and the two write paths (split by
    /// parameter count on the sync path; grouped by table, then bulk copied or run as commands in
    /// order on the async path). A provider supplies the connection and overrides the hooks its
    /// engine needs.
    /// </summary>
    /// <typeparam name="TSelf">The provider's context type.</typeparam>
    public abstract class DbDataContext<TSelf> : IDataContext, ISaveAcceptance, IStatementTimeout, IDisposable, IAsyncDisposable where TSelf : DbDataContext<TSelf>
    {
        private readonly ConcurrentDictionary<Guid, TSelf> _childContexts = new();
        private readonly RelationalDataProvider _provider;

        private bool _useDependency = false;
        private bool _useTransaction = false;
        private bool _disposed = false;
        // On a root, whether the tree is doomed: a failure, or a rollback asked for anywhere in it.
        private bool _needsRollback = false;
        // On a child, whether it committed its part; on a root, whether it committed the tree.
        private bool _committed = false;
        // On a root, whether its transaction has ended, committed or rolled back.
        private bool _completed = false;
        private bool _isInitialized = false;
        private DbTransaction? _transaction;
        private DbCommand? _command;
        private TSelf? _parent;
        // The root of a child's tree, whose connection and transaction it uses; null on a root.
        private TSelf? _root;
        private DbConnection? _connection;
        // On a root, its share of the provider's connection budget; released when it closes.
        private ConnectionSlot? _slot;
        // On a root, what the tree's saves committed in memory, put back if the transaction does
        // not commit. Created by the first save.
        private CommitUndoLog? _undo;
        // True on a context whose caller checks the saved objects itself, before, and marks them
        // saved itself, after the commit.
        private bool _callerManagesObjects;
        // The context's own statement time limit from its ContextOptions; null takes its parent's.
        private int? _statementTimeoutSeconds;

        /// <summary>Identifies this context in the pool and in diagnostics.</summary>
        protected Guid ContextId { get; } = Guid.NewGuid();

        protected DbDataContext(RelationalDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>The provider that created this context.</summary>
        protected RelationalDataProvider Provider => _provider;

        protected RelationalProviderOptions Options => _provider.Options;

        /// <summary>The context's connection; a child's is its root's.</summary>
        protected DbConnection? Connection => Root._connection;
        /// <summary>The context's transaction; a child's is its root's. Null once it has ended.</summary>
        protected DbTransaction? Transaction => Root._transaction;
        protected DbCommand? Command => _command;

        /// <summary>The context whose connection and transaction this one runs on: itself, unless it is a child.</summary>
        private TSelf Root => _root ?? (TSelf)this;

        public bool HasTransaction => _useTransaction;
        public bool WatchesForChanges => _useDependency;

        /// <summary>The old name of <see cref="WatchesForChanges"/>.</summary>
        [Obsolete("Use WatchesForChanges.")]
        public bool HasDependency => _useDependency;
        private bool HasChildren => !_childContexts.IsEmpty;
        /// <summary>The provider's free connection slots, shared by its root contexts and its change subscriptions.</summary>
        public int ConnectionsAvailable => _provider.ConnectionSlots.Available;

        // ---- Hooks a provider implements or overrides.

        /// <summary>
        /// A new, closed connection that does not enlist in an ambient transaction, transactional
        /// or not, built from one connection string so the driver keeps one pool, sized to
        /// <see cref="RelationalProviderOptions.MaxConnectionPoolSize"/>.
        /// </summary>
        protected abstract DbConnection BuildConnection(bool useTransaction);

        /// <summary>No longer called: there is no parallel write path.</summary>
        [Obsolete("No longer called. An oversized write runs its parts in order on its own context; a child context is opened with ContextOptions.Parent.")]
        protected virtual Task<TSelf> CreateChildContextAsync(CancellationToken token) =>
            throw new NotSupportedException("There is no parallel write path; open a child with ContextOptions.Parent.");

        /// <summary>The connection is open; start whatever the dependency option needs.</summary>
        protected virtual void OnOpened() { }

        /// <summary>The context is being disposed; stop whatever <see cref="OnOpened"/> started.</summary>
        protected virtual void OnDisposing() { }

        /// <summary>A command is about to run with its text set.</summary>
        protected virtual void PrepareCommand(DbCommand command) { }

        /// <summary>True when an opening failure is transient and worth another attempt; the default asks the provider's <see cref="RelationalDataProvider.ClassifyFailure"/>.</summary>
        protected virtual bool IsTransientOpenFailure(Exception exception) => _provider.ClassifyFailure(exception).IsTransient;

        /// <summary>How many times opening is attempted when failures are transient; the default is <see cref="RelationalProviderOptions.ConnectAttempts"/>.</summary>
        protected virtual int OpenAttempts => Math.Max(1, Options.ConnectAttempts);

        /// <summary>
        /// Whether the driver's command timeout limits a running statement, so that
        /// <see cref="RelationalProviderOptions.StatementTimeoutSeconds"/> can be set as it. An
        /// engine whose command timeout means something else, such as a wait for a lock, returns
        /// false and its statements run with the timeout its connection gives them.
        /// </summary>
        protected virtual bool AppliesStatementTimeout => true;

        /// <summary>
        /// Binds one value of <paramref name="source"/> (one element of a multi-valued term has its
        /// own name); the default sets the value as given.
        /// </summary>
        protected virtual DbParameter CreateParameter(DbCommand command, string name, object? value, IQueryParameter source)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"{_provider.ParameterPrefix}{name}";
            parameter.Value = value;
            return parameter;
        }

        /// <summary>Copies the current row into <paramref name="row"/>, in <paramref name="schema"/>'s column order.</summary>
        protected virtual void ReadRow(DbDataReader reader, object?[] row, IDataTableSchema? schema) => reader.GetValues(row!);

        /// <summary>
        /// Copies the current row of a row read into <paramref name="row"/>: value i is
        /// <paramref name="columns"/>[i], converted by its column where it has one, as
        /// <see cref="ReadRow"/> converts a loaded value. The default keeps the values as read.
        /// </summary>
        protected virtual void ReadResultRow(DbDataReader reader, object?[] row, IReadOnlyList<ResultColumn> columns) => reader.GetValues(row!);

        /// <summary>The names a read's rows are labelled with: its result columns on a row read, otherwise the table's properties.</summary>
        private static string[] RowNames(Query query) => query is SelectQuery { ResultColumns: { } columns }
            ? [.. columns.Select(c => c.Name)]
            : [.. query.TableSchema!.Columns.Select(x => x.PropertySchema.Name)];

        /// <summary>Reads the current row as <see cref="RowNames"/> labels it.</summary>
        private void ReadAnyRow(DbDataReader reader, object?[] row, Query query)
        {
            if (query is SelectQuery { ResultColumns: { } columns })
                ReadResultRow(reader, row, columns);
            else
                ReadRow(reader, row, query.TableSchema);
        }

        /// <summary>
        /// A value a write returned for <paramref name="column"/>, as the column's property expects
        /// it, as <see cref="ReadRow"/> converts a loaded value. The default keeps it as read.
        /// </summary>
        protected virtual object? ReadOutputValue(object? value, IDataColumnSchema? column) => value;

        /// <summary>Hands a value the server returned to its output term, converted for the property.</summary>
        protected void ReceiveOutput(OutputParameter output, object? value) =>
            output.SetReceivedOutput(ReadOutputValue(value is DBNull ? null : value, output.ColumnSchema));

        /// <summary>
        /// Reads what a bulk statement returned for its output columns: one row per written row,
        /// the identity first, then <see cref="BulkOutputs.Columns"/> in order. Each value goes to
        /// the output term of the row with that identity.
        /// </summary>
        protected async Task ReceiveBulkOutputsAsync(DbDataReader reader, BulkOutputs outputs, CancellationToken token)
        {
            while (await reader.ReadAsync(token))
            {
                if (!outputs.ByIdentity.TryGetValue(reader.GetGuid(0), out var row))
                    continue;
                for (var i = 1; i < reader.FieldCount; i++)
                    if (row.TryGetValue(outputs.Columns[i - 1], out var output))
                        ReceiveOutput(output, reader.GetValue(i));
            }
        }

        /// <summary>True when the provider can write this table's oversized batch with its bulk engine.</summary>
        protected virtual bool CanUseBulkPath(Query query) => false;

        /// <summary>Writes an oversized insert or update batch with the provider's bulk engine.</summary>
        protected virtual Task ExecuteBulkAsync(Query query, CancellationToken token) =>
            throw new NotSupportedException($"{GetType().Name} has no bulk engine.");

        /// <summary>No longer read: every engine runs an oversized write's parts in order on one context.</summary>
        [Obsolete("No longer read. Every engine runs an oversized write the bulk engine cannot take as commands in order on its own context.")]
        protected virtual bool AllowsConcurrentWriters => true;

        /// <summary>The connection in diagnostics.</summary>
        protected virtual string DescribeConnection() => Connection?.GetHashCode().ToString() ?? "null connection";

        #region Opening

        /// <summary>Opens this context: takes a pool slot, opens the connection, begins its transaction and enlists with a parent.</summary>
        protected void Open(TSelf? parent, bool useTransaction, bool useDependency) =>
            OpenCore(parent, useTransaction, useDependency, async: false, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc cref="Open"/>
        protected Task OpenAsync(TSelf? parent, bool useTransaction, bool useDependency, CancellationToken token) =>
            OpenCore(parent, useTransaction, useDependency, async: true, token).AsTask();

        private async ValueTask OpenCore(TSelf? parent, bool useTransaction, bool useDependency, bool async, CancellationToken token)
        {
            if (_isInitialized)
                throw new Exception("Context has already been initialized");
            if (parent != null)
            {
                OpenChild(parent);
                return;
            }

            // No connection enlists, so a transaction here would commit outside the caller's
            // scope and survive its rollback. Refused rather than silently escaping it.
            if (useTransaction && System.Transactions.Transaction.Current != null)
                throw new DataContextException(
                    "A transactional context cannot be opened inside an ambient transaction (a TransactionScope): its own transaction would commit " +
                    "even if the scope rolled back. Open the context outside the scope, and make the context's transaction the unit of work.",
                    DataContextErrorKind.AmbientTransaction);

            var slots = _provider.ConnectionSlots;
            _slot = async ? await slots.AcquireAsync(token) : slots.Acquire();
            Debug.WriteLine($"Data Context Info: {slots.Available} of {slots.Capacity} connection slots available", "Database");

            _useTransaction = useTransaction;
            _useDependency = useDependency;
            var attempts = new StrongBox<int>(1);
            try
            {
                _connection = BuildConnection(useTransaction);
                await OpenConnection(async, attempts, token);
                _command = _connection.CreateCommand();
                if (HasTransaction)
                    _transaction = async ? await _connection.BeginTransactionAsync(token) : _connection.BeginTransaction();
                if (HasTransaction)
                    _command.Transaction = _transaction;
            }
            catch (Exception ex)
            {
                // A context that never opened must not keep its slot.
                ReleaseSlot();
                if (_connection != null)
                {
                    if (async)
                        await _connection.DisposeAsync();
                    else
                        _connection.Dispose();
                    _connection = null;
                }
                if (ex is OperationCanceledException || !DatabaseFailures.IsDatabaseFailure(ex))
                    throw;
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException($"Opening a connection to the {_provider.EngineDisplayName} database was cancelled.", ex, token);
                throw _provider.TranslateFailure(
                    $"Unable to open a connection to the {_provider.EngineDisplayName} database ({Describe(attempts.Value)}). {ex.Message}",
                    ex, DatabaseOperation.Open, mayHaveBeenApplied: false, attempts.Value);
            }
            if (_useDependency)
                OnOpened();
            Debug.WriteLine($"Created database object {ContextId} using connection {DescribeConnection()} at {DateTime.Now:H:mm:ss.ff} {(HasTransaction ? "Using a transaction." : "")}"
                , "Database");
            _isInitialized = true;
        }

        // Every attempt shares the connection's own connect timeout, so retries never make an open
        // wait longer than the timeout it was configured with.
        private ValueTask OpenConnection(bool async, StrongBox<int> attempts, CancellationToken token) =>
            ConnectionOpener.OpenAsync(_connection!, async, OpenAttempts, IsTransientOpenFailure, ConnectionOpener.TimeoutOf(_connection!), attempts, token);

        private static string Describe(int attempts) => attempts == 1 ? "1 attempt" : $"{attempts} attempts";

        private void ReleaseSlot()
        {
            _slot?.Dispose();
            _slot = null;
        }

        /// <summary>
        /// Opens a child on its root's connection and transaction, so the tree commits or rolls
        /// back as one transaction. It takes no pool slot and owns neither; it has a command of
        /// its own, and must not be used while another context of the tree is running one.
        /// </summary>
        private void OpenChild(TSelf parent)
        {
            if (parent.GetType() != GetType())
                throw new Exception("Parent object must be of type DataContext");
            if (!parent.HasTransaction)
                throw new Exception("Unable to enlist transaction from parent Database object. HasTransaction is false.");
            var root = parent.Root;
            ObjectDisposedException.ThrowIf(root._disposed, root);
            root.EnsureActive();
            _parent = parent;
            _root = root;
            _useTransaction = true;
            _useDependency = root._useDependency;
            _command = root._connection!.CreateCommand();
            _command.Transaction = root._transaction;
            // The root counts every open context of the tree; a parent below it, its own.
            root._childContexts.TryAdd(ContextId, (TSelf)this);
            if (parent != root)
                parent._childContexts.TryAdd(ContextId, (TSelf)this);
            Debug.WriteLine($"Created database object {ContextId} on the connection of {root.ContextId}, with {root._childContexts.Count} child contexts", "Database");
            _isInitialized = true;
        }

        #endregion

        #region IDataContext Implementation
        bool IDataContext.HasTransaction => HasTransaction;
        bool IDataContext.WatchesForChanges => _useDependency;
        bool IDataContext.HasDependency => _useDependency;
        bool IDataContext.HasChildren => HasChildren;
        IDataContext? IDataContext.Parent => _parent;

        DataSet IDataContext.GetDataSet(IQuery query) => GetDataSet(query);
        Task<DataSet> IDataContext.GetDataSetAsync(IQuery query) => GetDataSetAsync(query, CancellationToken.None);
        Task<DataSet> IDataContext.GetDataSetAsync(IQuery query, CancellationToken token) => GetDataSetAsync(query, token);
        IAsyncEnumerable<StreamingRow> IDataContext.StreamAsync(IQuery query, CancellationToken ct) => StreamAsync(query, ct);

        void IDataContext.ExecuteQuery(IQuery query) => ExecuteSQL(query);
        Task IDataContext.ExecuteQueryAsync(IQuery query) => ExecuteSQLAsync(query, CancellationToken.None);
        Task IDataContext.ExecuteQueryAsync(IQuery query, CancellationToken token) => ExecuteSQLAsync(query, token);

        void IDataContext.ExecuteQueries(IQuery[] queries)
        {
            foreach (var query in queries)
                ExecuteSQL(query);
        }
        Task IDataContext.ExecuteQueriesAsync(IQuery[] queries) => ExecuteQueriesAsync(queries, CancellationToken.None);
        Task IDataContext.ExecuteQueriesAsync(IQuery[] queries, CancellationToken token) => ExecuteQueriesAsync(queries, token);

        private async Task ExecuteQueriesAsync(IQuery[] queries, CancellationToken token)
        {
            foreach (var query in queries)
                await ExecuteSQLAsync(query, token);
        }

        void IDataContext.Save(ICoreObject obj)
        {
            foreach (var query in BuildSaveQueries(obj))
                ExecuteSQL(query);
            Accept([obj]);
        }
        void IDataContext.Delete(ICoreObject obj) => ExecuteSQL(BuildDeleteQuery(obj));

        Task IDataContext.SaveAsync(ICoreObject obj) => SaveAsync(obj, CancellationToken.None);
        Task IDataContext.SaveAsync(ICoreObject obj, CancellationToken token) => SaveAsync(obj, token);

        private async Task SaveAsync(ICoreObject obj, CancellationToken token)
        {
            foreach (var query in BuildSaveQueries(obj))
                await ExecuteSQLAsync(query, token);
            Accept([obj]);
        }

        /// <summary>
        /// Marks the objects a save has written as saved. Without a transaction each write has
        /// already committed. With one, the root's <see cref="CommitUndoLog"/> records what this
        /// changed, and <see cref="EndTransaction"/> forgets it when the transaction commits or puts
        /// it back when it does not, so a rollback leaves the objects as they were before their
        /// first save in it, with any change made since still pending.
        /// </summary>
        private void Accept(IEnumerable<ICoreObject?> objs)
        {
            if (_callerManagesObjects)
                return;
            try
            {
                if (!HasTransaction)
                {
                    foreach (var obj in objs)
                        obj?.CommitChanges();
                    return;
                }
                var undo = Root._undo ??= new CommitUndoLog();
                foreach (var obj in objs)
                    obj?.CommitChangesInTransaction(undo);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        bool ISaveAcceptance.CallerManagesObjects
        {
            set => _callerManagesObjects = value;
        }

        int IStatementTimeout.StatementTimeoutSeconds
        {
            set => _statementTimeoutSeconds = value;
        }

        /// <summary>The limit of each statement this context runs: its own, else its parent's, else the provider's.</summary>
        private int StatementTimeoutSeconds => _statementTimeoutSeconds ?? _parent?.StatementTimeoutSeconds ?? Options.StatementTimeoutSeconds;
        Task IDataContext.DeleteAsync(ICoreObject obj) => ExecuteSQLAsync(BuildDeleteQuery(obj), CancellationToken.None);
        Task IDataContext.DeleteAsync(ICoreObject obj, CancellationToken token) => ExecuteSQLAsync(BuildDeleteQuery(obj), token);

        /// <summary>
        /// What saving the object runs, from the provider's one save builder: deletes of removed
        /// children, forceSave link writes, then the object's insert or update with its children.
        /// The object is validated first.
        /// </summary>
        private List<Query> BuildSaveQueries(ICoreObject obj)
        {
            EnsureActive();
            try
            {
                ArgumentNullException.ThrowIfNull(obj, nameof(obj));
                obj.PrepareForSave();
                return _provider.CreateSaveQueries([obj]);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        /// <summary>
        /// What a save of a batch of objects of any types runs, each type's inserts and updates as
        /// separate chains. Every object is validated first.
        /// </summary>
        private List<Query> BuildBatchSaveQueries(IEnumerable<ICoreObject?>? objects)
        {
            EnsureActive();
            try
            {
                var list = objects as IList<ICoreObject?> ?? objects?.ToList();
                if (!_callerManagesObjects)
                    SaveChecks.Run(list);
                return _provider.CreateSaveQueries(list, separateOperations: true);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        /// <summary>The object's row and every row of its child collections, loaded or not.</summary>
        private DeleteWhereQuery BuildDeleteQuery(ICoreObject obj)
        {
            EnsureActive();
            try
            {
                ArgumentNullException.ThrowIfNull(obj, nameof(obj));
                var tableSchema = _provider.GetTableSchema(obj.GetType());
                if (!tableSchema.ObjectSchema.IsDeletable)
                    throw new NotSupportedException($"Type {tableSchema.ObjectSchema.FullName} is not [Deletable]; deletion is opt-in.");
                return _provider.CreateDeleteByIdQuery(tableSchema, [obj.ID])!;
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        int IDataContext.DeleteWhere<T>(Expression<Func<T, bool>> predicate)
        {
            EnsureActive();
            try
            {
                ArgumentNullException.ThrowIfNull(predicate);
                if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                    return 0;
                return ExecuteDelete(_provider.CreateDeleteWhereQuery<T>(predicate.Body));
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        async Task<int> IDataContext.DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token)
        {
            EnsureActive();
            try
            {
                ArgumentNullException.ThrowIfNull(predicate);
                if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                    return 0;
                return await ExecuteDeleteAsync(_provider.CreateDeleteWhereQuery<T>(predicate.Body), token);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        void IDataContext.Commit() => Commit();
        Task IDataContext.CommitAsync(CancellationToken cancellationToken) => CommitAsync(cancellationToken);
        void IDataContext.Rollback() => Rollback();
        Task IDataContext.RollbackAsync(CancellationToken cancellationToken) => RollbackAsync(cancellationToken);
        void IDataContext.TryRollback() => TryRollback();
        Task IDataContext.TryRollbackAsync() => TryRollbackAsync();
        // A catch block's rollback: it never throws, a cancelled token included.
        Task IDataContext.TryRollbackAsync(CancellationToken token) => TryRollbackAsync();

        void IDataContext.Save(IEnumerable<ICoreObject?> objs) => SaveBatch(objs);
        Task IDataContext.SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken) => SaveBatchAsync(objs, cancellationToken);

        // The obsolete names of the batch save. They always ran the same statements a save runs;
        // which engine writes them was never the caller's choice.
        void IDataContext.BulkSave(IEnumerable<ICoreObject?>? objects) => SaveBatch(objects);
        Task IDataContext.BulkSaveAsync(IEnumerable<ICoreObject?>? objects) => SaveBatchAsync(objects, CancellationToken.None);
        Task IDataContext.BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token) => SaveBatchAsync(objects, token);

        private void SaveBatch(IEnumerable<ICoreObject?>? objects)
        {
            var list = objects as IList<ICoreObject?> ?? objects?.ToList();
            foreach (var query in BuildBatchSaveQueries(list))
                ExecuteSQL(query);
            if (list != null)
                Accept(list);
        }

        private async Task SaveBatchAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token)
        {
            var list = objects as IList<ICoreObject?> ?? objects?.ToList();
            // ExecuteSQLAsync routes large parameter counts to the provider's bulk engine. Each
            // type's inserts and updates are separate chains, which is the shape it batches by.
            foreach (var query in BuildBatchSaveQueries(list))
                await ExecuteSQLAsync(query, token);
            if (list != null)
                Accept(list);
        }

        /// <summary>
        /// Loads a lazy collection of an object this context loaded, through this context's
        /// provider, on a context of its own: the read happens on first access, when this
        /// context may be gone or mid-transaction.
        /// </summary>
        private void LoadLazyCollection(TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection collection) =>
            ChildCollectionLoader.Load(_provider, collection, LinkLoader);

        private TFM.Nucleus.Abstractions.Interfaces.ILinkedObjectLoader? _linkLoader;

        /// <summary>
        /// What the [LinkedObject] properties of the objects this context loads are read through:
        /// this context's provider, on a context of its own, for the same reason as a lazy collection.
        /// </summary>
        private TFM.Nucleus.Abstractions.Interfaces.ILinkedObjectLoader LinkLoader => _linkLoader ??= new ProviderLinkedObjectLoader(_provider);

        IEnumerable<T> IDataContext.Load<T>(Expression expression) => Load<T>(expression);
        public INucleusCollection<T> Load<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            EnsureActive();
            try
            {
                if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                    return new NucleusCollection<T>();
                var query = _provider.ParseSelectExpression<T>(expression);
                return ChildGraphBinder.BuildCollection<T>(query, GetDataSet(query), LoadLazyCollection, LinkLoader, []);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        async Task<IEnumerable<T>> IDataContext.LoadAsync<T>(Expression expression) => await LoadAsync<T>(expression);
        public async Task<INucleusCollection<T>> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            EnsureActive();
            try
            {
                if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                    return new NucleusCollection<T>();
                var query = _provider.ParseSelectExpression<T>(expression);
                return ChildGraphBinder.BuildCollection<T>(query, await GetDataSetAsync(query, CancellationToken.None), LoadLazyCollection, LinkLoader, []);
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }
        #endregion

        #region Disposing

        public void Dispose()
        {
            Debug.WriteLine($"Attempting to dispose of database object {ContextId} at {DateTime.Now:H:mm:ss.ff}{(HasChildren ? " with " + _childContexts.Values.Count + " child db's" : "")}", "Database");
            if (_disposed)
                return;
            _command?.Dispose();
            _command = null;
            DisposeCore(async: false).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            if (_command != null)
                await _command.DisposeAsync().ConfigureAwait(false);
            _command = null;
            await DisposeCore(async: true).ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// A child leaves the connection and the transaction to its root, and only reports whether
        /// it committed its part. A root ends its transaction if a commit has not, then closes the
        /// connection and leaves the pool.
        /// </summary>
        private async ValueTask DisposeCore(bool async)
        {
            // Disposed even when the commit throws: FinallyDispose has released everything by
            // then, and a second dispose must not try again.
            try
            {
                if (_root != null)
                    LeaveTree();
                else
                {
                    if (_useDependency && _connection != null)
                        OnDisposing();
                    await FinallyDispose(async).ConfigureAwait(false);
                }
            }
            finally
            {
                _disposed = true;
            }
            Debug.WriteLine($"Diposed of database object {ContextId} at {DateTime.Now:H:mm:ss.ff}", "Database");
        }

        /// <summary>
        /// A child that did not commit its part dooms the tree when commits are required, as a
        /// scope left without completing does. Otherwise the root decides, as it always has.
        /// </summary>
        private void LeaveTree()
        {
            var root = _root!;
            if (!_committed && !root._completed && Options.RequireCommit)
                root._needsRollback = true;
            root._childContexts.TryRemove(ContextId, out _);
            _parent?._childContexts.TryRemove(ContextId, out _);
        }

        /// <summary>
        /// Ends a transaction a commit has not already ended, then closes the connection and leaves
        /// the pool. A context marked for rollback rolls back. One never committed rolls back when
        /// commits are required, and otherwise commits, as every context once did, with a warning.
        /// A commit or rollback that throws still releases the transaction, the connection and the
        /// pool slot before its failure propagates, so a failed one cannot leak them.
        /// </summary>
        private async ValueTask FinallyDispose(bool async)
        {
            try
            {
                if (HasTransaction && !_completed)
                {
                    var commit = !_needsRollback && !Options.RequireCommit;
                    if (commit)
                        _provider.Logger?.LogWarning(
                            "A transactional context was disposed without Commit or Rollback, so it committed. " +
                            "Call Commit or CommitAsync when the work is done: with RequireCommit set, or from v3, a context disposed without a commit rolls back.");
                    await EndTransaction(commit, async).ConfigureAwait(false);
                }
                _childContexts.Clear();
            }
            finally
            {
                try
                {
                    if (_connection != null)
                    {
                        Debug.WriteLine($"Disposing of database connection {DescribeConnection()} at {DateTime.Now:H:mm:ss.ff}", "Database");
                        if (async)
                        {
                            await _connection.CloseAsync().ConfigureAwait(false);
                            await _connection.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _connection.Close();
                            _connection.Dispose();
                        }
                        _connection = null;
                    }
                }
                finally
                {
                    ReleaseSlot();
                }
            }
        }

        /// <summary>
        /// Commits or rolls back a root's transaction and marks it ended, so nothing runs on the
        /// tree after it. Ended even when the commit or rollback throws: the transaction cannot be
        /// used again, and disposing it lets the server roll back whatever it still holds. The
        /// objects the tree's saves marked saved stay saved only if the commit returned; after a
        /// rollback or a failed commit they are put back before the failure propagates.
        /// </summary>
        private async ValueTask EndTransaction(bool commit, bool async)
        {
            _completed = true;
            var transaction = _transaction;
            var committed = false;
            try
            {
                if (transaction == null)
                    return;
                await CommitOrRollBack(transaction, commit, async).ConfigureAwait(false);
                committed = commit;
            }
            finally
            {
                var undo = _undo;
                _undo = null;
                if (committed)
                    undo?.Clear();
                else
                    undo?.Revert();
            }
        }

        /// <summary>
        /// Commits or rolls back the transaction. A commit that fails is reported as
        /// <see cref="DatabaseErrorKind.CommitOutcomeUnknown"/>, which may have been applied, unless
        /// the engine says it rejected the transaction; a rollback that fails applied nothing.
        /// </summary>
        private async ValueTask CommitOrRollBack(DbTransaction transaction, bool commit, bool async)
        {
            try
            {
                if (commit)
                {
                    if (async)
                        await transaction.CommitAsync().ConfigureAwait(false);
                    else
                        transaction.Commit();
                }
                else
                {
                    if (async)
                        await transaction.RollbackAsync().ConfigureAwait(false);
                    else
                        transaction.Rollback();
                    Debug.WriteLine($"Database object {ContextId} rolled back at {DateTime.Now:H:mm:ss.ff}", "Database");
                }
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                if (!commit)
                    throw _provider.TranslateFailure($"The rollback failed; the transaction was not committed. {ex.Message}",
                        ex, DatabaseOperation.Rollback, mayHaveBeenApplied: false);
                var unknown = DatabaseFailures.Resolve(_provider.ClassifyFailure(ex), DatabaseOperation.Commit, mayHaveBeenApplied: true).MayHaveBeenApplied;
                throw _provider.TranslateFailure(unknown
                        ? $"The commit failed, and the database may or may not have kept the transaction. {ex.Message}"
                        : $"The database refused the transaction at its commit and rolled it back. {ex.Message}",
                    ex, DatabaseOperation.Commit, mayHaveBeenApplied: true);
            }
            finally
            {
                if (async)
                    await transaction.DisposeAsync().ConfigureAwait(false);
                else
                    transaction.Dispose();
                _transaction = null;
            }
        }

        /// <inheritdoc cref="IDataContext.Commit"/>
        public void Commit() => CommitCore(async: false, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc cref="IDataContext.Commit"/>
        public Task CommitAsync(CancellationToken cancellationToken = default) => CommitCore(async: true, cancellationToken).AsTask();

        private async ValueTask CommitCore(bool async, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!HasTransaction)
                return;
            if (Root._completed && Root._needsRollback)
                throw new DataContextException(
                    "The transaction was rolled back, so there is nothing to commit.",
                    DataContextErrorKind.TransactionDoomed);
            EnsureActive();
            if (_root != null)
            {
                _committed = true;
                return;
            }
            if (!_childContexts.IsEmpty)
                throw new DataContextException(
                    $"Cannot commit: {_childContexts.Count} child context(s) of this transaction are still open. Commit and dispose them first.",
                    DataContextErrorKind.ChildContextOpen);
            if (_needsRollback)
            {
                await EndTransaction(commit: false, async).ConfigureAwait(false);
                throw new DataContextException(
                    "The transaction was rolled back instead of committed: a failure or a rollback marked it for rollback.",
                    DataContextErrorKind.TransactionDoomed);
            }
            // A commit is not abandoned half way, so the token is honoured only before it starts.
            // A cancelled commit rolls back, or disposing the context could commit what the caller
            // just cancelled.
            if (cancellationToken.IsCancellationRequested)
            {
                await RollbackCore(async).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _committed = true;
            await EndTransaction(commit: true, async).ConfigureAwait(false);
        }

        /// <summary>
        /// Rolls the tree's transaction back at once, from the root or from any child: there is
        /// nothing left to do in it, so it ends, its locks are released and the objects its saves
        /// marked saved are put back. Every context of the tree, the root included, then finds the
        /// transaction ended, and a commit of any of them throws. Does nothing once the
        /// transaction has ended.
        /// </summary>
        public void Rollback() => RollbackCore(async: false).GetAwaiter().GetResult();

        /// <inheritdoc cref="Rollback"/>
        /// <remarks>A cancelled token still rolls back, and then throws.</remarks>
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await RollbackCore(async: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// <see cref="Rollback"/> for a catch block: a rollback that fails is logged, not thrown, so
        /// the failure being handled propagates. The server rolls back what the failed rollback
        /// left when the connection closes.
        /// </summary>
        public void TryRollback() => TryRollbackCore(async: false).GetAwaiter().GetResult();

        /// <inheritdoc cref="TryRollback"/>
        public Task TryRollbackAsync() => TryRollbackCore(async: true).AsTask();

        private async ValueTask TryRollbackCore(bool async)
        {
            try
            {
                await RollbackCore(async).ConfigureAwait(false);
            }
            catch (DatabaseException ex)
            {
                _provider.Logger?.LogWarning(ex, "A rollback failed; the transaction was not committed. {error}", ex.Message);
            }
        }

        private async ValueTask RollbackCore(bool async)
        {
            if (Root._completed)
                return;
            MarkForRollback();
            // A child runs on its root's transaction, which the root holds and ends.
            if (HasTransaction)
                await Root.EndTransaction(commit: false, async).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks the transaction tree for rollback without ending it: the root rolls back when it
        /// is committed or disposed. The failure paths use this, because an exception filter must
        /// not run a command.
        /// </summary>
        private void MarkForRollback()
        {
            var root = Root;
            if (root._completed)
                return;
            _needsRollback = true;
            root._needsRollback = true;
        }

        /// <summary>
        /// For an exception filter: marks a transactional context for rollback on any failure out
        /// of one of its operations, and returns false so the failure propagates as it was thrown.
        /// </summary>
        private bool Doom()
        {
            if (HasTransaction)
                MarkForRollback();
            return false;
        }

        /// <summary>Refuses to run anything on a tree whose transaction has ended.</summary>
        private void EnsureActive()
        {
            if (_useTransaction && Root._completed)
                throw new DataContextException(
                    "The context's transaction has already been committed or rolled back; open a new context.",
                    DataContextErrorKind.TransactionCompleted);
        }

        #endregion

        #region Commands and errors

        /// <summary>Binds each term that uses a variable, expanding multi-valued terms.</summary>
        protected void BindParameters(DbCommand command, IEnumerable<IQueryParameter?>? terms)
        {
            if (terms == null)
                return;
            foreach (var term in terms)
            {
                if (term == null || !term.UsesVariable)
                    continue;
                foreach (var bound in ParameterBinding.Bind(term))
                    command.Parameters.Add(CreateParameter(command, bound.Key, bound.Value, term));
            }
        }

        /// <summary>
        /// Sets the command's text, time limit and parameters for a run; deletes skip the provider's
        /// hook. The limit is set on every run because the command is shared: the bulk engine and
        /// schema statements set their own on it, which must not carry over to the next statement.
        /// </summary>
        private DbCommand Prepare(string commandText, IEnumerable<IQueryParameter?>? terms, bool hook = true)
        {
            var command = _command ?? throw new DataContextException("No active Command", DataContextErrorKind.CurrentCommandIsNull);
            command.CommandText = commandText;
            command.Parameters.Clear();
            ApplyStatementTimeout(command);
            if (hook)
                PrepareCommand(command);
            BindParameters(command, terms);
            return command;
        }

        /// <summary>A command of its own for a read, on this context's transaction.</summary>
        private DbCommand NewReadCommand()
        {
            var connection = Connection
                ?? throw new DataContextException("No active Connection", DataContextErrorKind.CurrentConnectionIsNull);
            _command = connection.CreateCommand();
            ApplyStatementTimeout(_command);
            if (HasTransaction && Transaction is { } transaction)
                _command.Transaction = transaction;
            return _command;
        }

        /// <summary>As <see cref="Failure(string, Exception, DatabaseOperation, CancellationToken)"/>, for an operation not recorded.</summary>
        protected Exception Failure(string message, Exception exception) =>
            Failure(message, exception, DatabaseOperation.Unknown, CancellationToken.None);

        /// <summary>As <see cref="Failure(string, Exception, DatabaseOperation, CancellationToken)"/>, for an operation not recorded.</summary>
        protected Exception Failure(string message, Exception exception, CancellationToken token) =>
            Failure(message, exception, DatabaseOperation.Unknown, token);

        /// <summary>
        /// Marks a transactional context for rollback, then gives the exception for a failure of
        /// <paramref name="operation"/>. A cancellation propagates as it was thrown, and so does
        /// a failure that is not the database's (one of Nucleus's own exceptions, or a defect).
        /// A database failure after <paramref name="token"/> was cancelled is the cancellation: an
        /// engine can report a command it cancelled as its own error (SqlClient raises a
        /// SqlException), so that becomes an <see cref="OperationCanceledException"/> with the
        /// engine's error inside. Any other database failure is translated by the provider
        /// (<see cref="RelationalDataProvider.TranslateFailure(string, Exception, DatabaseOperation, bool, int)"/>):
        /// a write on a context with no transaction may have been applied, since each statement
        /// commits as it runs; on a transactional context nothing commits after a failure.
        /// </summary>
        protected Exception Failure(string message, Exception exception, DatabaseOperation operation, CancellationToken token)
        {
            if (HasTransaction)
                MarkForRollback();
            if (exception is OperationCanceledException || !DatabaseFailures.IsDatabaseFailure(exception))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
            if (token.IsCancellationRequested)
                return new OperationCanceledException(message, exception, token);
            var mayHaveBeenApplied = !HasTransaction && operation is not (DatabaseOperation.Read or DatabaseOperation.Open or DatabaseOperation.Rollback);
            return _provider.TranslateFailure(message, exception, operation, mayHaveBeenApplied);
        }

        /// <summary>The exception for a failure of a schema read or statement run on this context, as <see cref="Failure(string, Exception, DatabaseOperation, CancellationToken)"/> gives it.</summary>
        internal Exception SchemaFailure(string message, Exception exception, CancellationToken token) =>
            Failure(message, exception, DatabaseOperation.Schema, token);

        /// <summary>
        /// Gives a command the schema statement time limit, where the engine's command timeout is
        /// one. Where it is the wait for a lock instead, the command keeps that wait.
        /// </summary>
        private void ApplySchemaTimeout(DbCommand command)
        {
            if (AppliesStatementTimeout)
                command.CommandTimeout = Options.SchemaCommandTimeoutSeconds;
        }

        /// <summary>Gives a command this context's statement time limit, where the engine's command timeout is one.</summary>
        private void ApplyStatementTimeout(DbCommand command)
        {
            if (AppliesStatementTimeout)
                command.CommandTimeout = StatementTimeoutSeconds;
        }

        #endregion

        #region Reads

        public DataSet GetDataSet(IQuery query) => GetDataSetCore(query, async: false, CancellationToken.None).GetAwaiter().GetResult();

        public Task<DataSet> GetDataSetAsync(IQuery query, CancellationToken token) => GetDataSetCore(query, async: true, token).AsTask();

        /// <summary>
        /// Reads the rows, then the total count when asked, then each child collection's result
        /// set. The connection stays open, so a transactional context can write after a read; it
        /// closes when the context is disposed.
        /// </summary>
        private async ValueTask<DataSet> GetDataSetCore(IQuery query, bool async, CancellationToken token)
        {
            EnsureActive();
            try
            {
                if (query == null || query is not Query dbQuery)
                    throw new DataContextException("Invalid Query type", DataContextErrorKind.InvalidIQueryType);
                var command = NewReadCommand();
                command.CommandText = dbQuery.BuildQuery();
                PrepareCommand(command);
                BindParameters(command, dbQuery.AllQueryParameters);
                var dataSet = dbQuery is SelectQuery { ResultColumns: not null }
                    ? DataSet.Initialize(dbQuery.TableName ?? "", RowNames(dbQuery))
                    : DataSet.Initialize(dbQuery.TableSchema);
                using var reader = async
                    ? await command.ExecuteReaderAsync(CommandBehavior.Default, token)
                    : command.ExecuteReader(CommandBehavior.Default);
                if (reader.HasRows)
                {
                    while (async ? await reader.ReadAsync(token) : reader.Read())
                        ReadAnyRow(reader, dataSet.GetNewRow(), dbQuery);
                }
                if (dbQuery is SelectQuery { WithTotalCount: true } &&
                    (async ? await reader.NextResultAsync(token) : reader.NextResult()) &&
                    (async ? await reader.ReadAsync(token) : reader.Read()))
                    dataSet.TotalRowCount = reader.GetInt64(0);
                if (dbQuery is SelectQuery)
                    await ReadChildResults(reader, dbQuery, dataSet, async, token);
                return dataSet;
            }
            catch (Exception ex)
            {
                throw Failure($"Error returning Query from DB. {ex.Message}", ex, DatabaseOperation.Read, token);
            }
        }

        /// <summary>
        /// Reads the result set of each child collection query, in the pre-order the batch
        /// renders them, into the data set's linked data sets.
        /// </summary>
        private async ValueTask ReadChildResults(DbDataReader reader, Query query, DataSet dataSet, bool async, CancellationToken token)
        {
            foreach (var childQuery in query.LinkedQueries)
            {
                if (!(async ? await reader.NextResultAsync(token) : reader.NextResult()))
                    throw new DataContextException($"The batch returned no result set for the child query on {childQuery.TableName}.", DataContextErrorKind.MissingResultSet);
                var childSet = DataSet.Initialize(childQuery.TableSchema);
                while (async ? await reader.ReadAsync(token) : reader.Read())
                    ReadRow(reader, childSet.GetNewRow(), childQuery.TableSchema);
                dataSet.LinkedDataSets.Add(childSet);
                await ReadChildResults(reader, childQuery, childSet, async, token);
            }
        }

        /// <summary>
        /// Streams the query's own rows. A select's child collection queries are left out of
        /// the command: the caller fetches them itself, and sending them here only transferred
        /// every child row to be drained unread.
        /// </summary>
        public async IAsyncEnumerable<StreamingRow> StreamAsync(IQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnsureActive();
            if (query == null || query is not Query dbQuery)
                throw new DataContextException("Invalid Query type", DataContextErrorKind.InvalidIQueryType);

            var command = NewReadCommand();
            command.CommandText = dbQuery is SelectQuery select
                ? select.BuildQuery(includeChildQueries: false)
                : dbQuery.BuildQuery();
            PrepareCommand(command);
            BindParameters(command, dbQuery.AllQueryParameters);

            var row = new StreamingRow(RowNames(dbQuery));

            DbDataReader? reader = null;
            try
            {
                reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);
            }
            catch (Exception ex)
            {
                throw Failure($"Error streaming Query from DB. {ex.Message}", ex, DatabaseOperation.Read, cancellationToken);
            }

            await using (reader)
            {
                // A failure reading a row dooms a transactional context, as a failed command does,
                // and is translated as a failed read is. An iterator cannot catch around a yield,
                // so the read is caught on its own, and the flag is false whenever a read is under
                // way and true only while the caller holds a row or once all are read: a caller
                // that stops early leaves it true.
                var settled = false;
                try
                {
                    while (true)
                    {
                        settled = false;
                        bool more;
                        try
                        {
                            more = reader.HasRows && await reader.ReadAsync(cancellationToken);
                            if (more)
                                ReadAnyRow(reader, row.Values, dbQuery);
                        }
                        catch (Exception ex)
                        {
                            throw Failure($"Error streaming Query from DB. {ex.Message}", ex, DatabaseOperation.Read, cancellationToken);
                        }
                        if (!more)
                            break;
                        settled = true;
                        yield return row;
                    }
                    settled = true;
                }
                finally
                {
                    if (!settled)
                        Doom();
                }
            }
        }

        /// <summary>
        /// Runs a provider-written read, such as a catalog query, on this context. Null when it
        /// returns no rows.
        /// </summary>
        protected internal async ValueTask<DbDataReader?> ReadStatementAsync(SchemaStatement statement, bool async, CancellationToken token)
        {
            EnsureActive();
            try
            {
                var command = Prepare(statement.Text, statement.Parameters);
                ApplySchemaTimeout(command);
                var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.Default, token) : command.ExecuteReader();
                if (!reader.HasRows)
                {
                    if (async)
                        await reader.DisposeAsync();
                    else
                        reader.Dispose();
                    return null;
                }
                return reader;
            }
            catch (Exception ex)
            {
                throw Failure($"Error returning Query from DB. {ex.Message}", ex, DatabaseOperation.Schema, token);
            }
        }

        #endregion

        #region Deletes

        /// <summary>
        /// Runs a delete batch and returns the rows of its root type affected, summed over its
        /// chunks. Each chunk is one command, never regrouped, split or bulk copied: its child
        /// statements select through the parent's condition and must run in the order rendered.
        /// </summary>
        public int ExecuteDelete(DeleteWhereQuery query) => ExecuteDeleteCore(query, async: false, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc cref="ExecuteDelete(DeleteWhereQuery)"/>
        public Task<int> ExecuteDeleteAsync(DeleteWhereQuery query, CancellationToken token) => ExecuteDeleteCore(query, async: true, token).AsTask();

        private async ValueTask<int> ExecuteDeleteCore(DeleteWhereQuery query, bool async, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(query);
            EnsureActive();
            try
            {
                // Every chunk is checked before any runs, so a refused delete deletes nothing.
                if (query.TableSchema?.ObjectSchema.HasLinkedCollections == true)
                    foreach (var chunk in query.FlattenLinkedQueries())
                        if (chunk is DeleteWhereQuery delete)
                            await EnsureNoLinkedMembersAsync(delete, async, token);
                var affected = 0;
                foreach (var chunk in query.FlattenLinkedQueries())
                {
                    var parameters = DeleteParameters(chunk);
                    try
                    {
                        var command = Prepare(chunk.BuildQuery(), parameters, hook: false);
                        affected += Convert.ToInt32(async ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar());
                    }
                    catch (Exception ex)
                    {
                        throw Failure($"Unable to execute delete on DB. {ex.Message}", ex, DatabaseOperation.Delete, token);
                    }
                }
                return affected;
            }
            catch (Exception) when (Doom())
            {
                throw;
            }
        }

        /// <summary>
        /// Refuses a delete of a one whose linked collections still have members, before anything
        /// is deleted: their keys would point at a row that no longer exists. The check reads in
        /// the delete's own transaction, so a member written earlier in it counts. Nucleus creates
        /// no foreign keys, so a member another transaction inserts at the same moment is not seen.
        /// </summary>
        private async ValueTask EnsureNoLinkedMembersAsync(DeleteWhereQuery delete, bool async, CancellationToken token)
        {
            foreach (var (collection, members) in _provider.LinkedMembersOf(delete))
            {
                var owner = delete.TableSchema!.ObjectSchema.Name;
                bool found;
                try
                {
                    var command = NewReadCommand();
                    command.CommandText = members.BuildQuery(includeChildQueries: false);
                    PrepareCommand(command);
                    BindParameters(command, members.AllQueryParameters);
                    await using var reader = async ? await command.ExecuteReaderAsync(token) : command.ExecuteReader();
                    found = async ? await reader.ReadAsync(token) : reader.Read();
                }
                catch (Exception ex)
                {
                    throw Failure($"Unable to check {owner}.{collection.Name} for members before deleting. {ex.Message}", ex, DatabaseOperation.Read, token);
                }
                if (found)
                    throw new InvalidOperationException(
                        $"Cannot delete {owner}: {owner}.{collection.Name} still has {collection.LinkedType!.Name} members, whose keys would point at a deleted row. " +
                        $"Delete them, or point them at another {owner}, first.");
            }
        }

        /// <summary>
        /// A chunk's own parameters, each declared once however many statements reference it.
        /// Throws before anything runs when they exceed the per-command limit.
        /// </summary>
        private ParameterCollection DeleteParameters(Query chunk)
        {
            var parameters = chunk.Parameters;
            var count = ParameterBinding.Bind(parameters).Count();
            if (count > Options.MaxParametersPerCommand)
                throw new InvalidQueryException(
                    $"A delete of {chunk.TableName} needs {count} parameters; the limit is {Options.MaxParametersPerCommand}. Narrow the predicate.");
            return parameters;
        }

        #endregion

        #region Writes

        /// <summary>Runs a query, split into commands under the parameter limit.</summary>
        public void ExecuteSQL(IQuery query)
        {
            EnsureActive();
            if (query is DeleteWhereQuery delete)
            {
                ExecuteDelete(delete);
                return;
            }
            try
            {
                foreach (var qry in query.Split(Options.MaxParametersPerCommand))
                    ExecuteWriteAsync(qry, async: false, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw Failure($"Unable to execute SQL on DB. {ex.Message}", ex, DatabaseOperation.Write, CancellationToken.None);
            }
        }

        /// <summary>
        /// Runs a query grouped by table. A group over the parameter limit goes to the provider's
        /// bulk engine when it can take it, or else runs as commands under the limit, in order, on
        /// this context; any other group runs as one command.
        /// </summary>
        public async Task ExecuteSQLAsync(IQuery query, CancellationToken token)
        {
            EnsureActive();
            if (query is DeleteWhereQuery delete)
            {
                await ExecuteDeleteAsync(delete, token);
                return;
            }
            try
            {
                if (query is not Query dbQuery)
                    throw new Exception("Only Query objects are supported for execution");

                //Split queries by type and size to determine execution path
                foreach (var queriesByType in dbQuery.GroupByTableSchema())
                {
                    // Bulk path: single connection, single transaction, no parameter limit.
                    // Must check BEFORE NeedsSplitting which mutates LinkedQueries.
                    if (queriesByType.GetParameterCount() > Options.MaxParametersPerCommand && CanUseBulkPath(queriesByType))
                    {
                        await ExecuteBulkAsync(queriesByType, token);
                    }
                    else if (queriesByType.NeedsSplitting(Options.MaxParametersPerCommand, out var parts))
                    {
                        // The parts run in order on this context's connection and transaction, so
                        // the write commits or rolls back whole. Parts on connections of their own
                        // ran in parallel, but each in a transaction of its own.
                        foreach (var part in parts!)
                            await ExecuteWriteAsync(part, async: true, token);
                    }
                    else
                    {
                        var timer = Stopwatch.StartNew();
                        var executionId = Guid.NewGuid();
                        Debug.WriteLine($"{DateTime.Now} Starting SQL execution {executionId} on context:{ContextId}");
                        await ExecuteWriteAsync(queriesByType, async: true, token);
                        Debug.WriteLine($"{DateTime.Now} Finished SQL execution {executionId} in {timer.ElapsedMilliseconds} ms");
                    }
                }
            }
            catch (Exception ex)
            {
                throw Failure($"Unable to execute SQL on DB. {ex.Message}", ex, DatabaseOperation.Write, token);
            }
        }

        /// <summary>
        /// Runs one write command. A write with output terms runs as a reader, and each column it
        /// returns is matched by its alias to a term's variable; any other write runs without one.
        /// </summary>
        private async ValueTask ExecuteWriteAsync(IQuery query, bool async, CancellationToken token)
        {
            var command = Prepare(query.ToString()!, query.AllQueryParameters);
            var outputs = query is Query q
                ? q.AllQueryOutputParameters.ToDictionary(o => o.Variable!, StringComparer.OrdinalIgnoreCase)
                : [];
            if (outputs.Count == 0)
            {
                if (async)
                    await command.ExecuteNonQueryAsync(token);
                else
                    command.ExecuteNonQuery();
                return;
            }
            await using var reader = async ? await command.ExecuteReaderAsync(token) : command.ExecuteReader();
            do
            {
                while (async ? await reader.ReadAsync(token) : reader.Read())
                    for (var i = 0; i < reader.FieldCount; i++)
                        if (outputs.TryGetValue(reader.GetName(i), out var output))
                            ReceiveOutput(output, reader.GetValue(i));
            }
            while (async ? await reader.NextResultAsync(token) : reader.NextResult());
        }

        /// <summary>Runs one provider-written statement, such as schema DDL, on this context.</summary>
        protected internal async ValueTask ExecuteStatementAsync(SchemaStatement statement, bool async, CancellationToken token)
        {
            EnsureActive();
            try
            {
                var command = Prepare(statement.Text, statement.Parameters);
                ApplySchemaTimeout(command);
                if (async)
                    await command.ExecuteNonQueryAsync(token);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw Failure($"Unable to execute SQL on DB. {ex.Message}", ex, DatabaseOperation.Schema, token);
            }
        }

        #endregion

        public override string ToString()
        {
            return $"Database object {ContextId} - using Connection {DescribeConnection()}";
        }
    }

    /// <summary>A failure of a data context itself, as opposed to the database.</summary>
    public class DataContextException : TFM.Nucleus.NucleusException
    {
        public DataContextErrorKind Kind { get; }

        public DataContextException(string message, DataContextErrorKind kind) : base(message)
        {
            Kind = kind;
        }
    }

    public enum DataContextErrorKind
    {
        UnableToCreateNewCommand = 0,
        CurrentCommandIsNull = 10,
        CurrentConnectionIsNull = 20,
        InvalidIQueryType = 30,
        /// <summary>A batch returned fewer result sets than the queries it was built from.</summary>
        MissingResultSet = 40,
        /// <summary>Every connection the provider allows (MaxConnectionPoolSize) was held by its contexts and change subscriptions, and none came free in time.</summary>
        PoolExhausted = 50,
        /// <summary>Something was run on a context whose transaction had already been committed or rolled back.</summary>
        TransactionCompleted = 60,
        /// <summary>A commit was asked of a transaction a failure or a rollback had marked; it rolled back instead.</summary>
        TransactionDoomed = 70,
        /// <summary>A commit was asked of a root while a child context of its transaction was still open.</summary>
        ChildContextOpen = 80,
        /// <summary>A transactional context was opened inside an ambient transaction (a TransactionScope), which its own transaction would escape.</summary>
        AmbientTransaction = 90,
        Unspecified = 99,
    }

    /// <summary>
    /// The transaction a table rebuild runs in: one transactional context, committed by
    /// <see cref="CommitAsync"/>. Columns are read through the transaction, so a renamed table
    /// is visible before it commits.
    /// </summary>
    public sealed class DbSchemaTransaction<TContext>(TContext context, Func<string, SchemaStatement> columnsQuery, Func<DbDataReader, CatalogColumn> readColumn)
        : ISchemaTransaction where TContext : DbDataContext<TContext>
    {
        public ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken) =>
            context.ExecuteStatementAsync(statement, async, cancellationToken);

        public async ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(string tableName, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var reader = await context.ReadStatementAsync(columnsQuery(tableName), async, cancellationToken)
                    ?? throw new Exception("The data reader was null");
                List<CatalogColumn> columns = [];
                while (async ? await reader.ReadAsync(cancellationToken) : reader.Read())
                    columns.Add(readColumn(reader));
                return columns;
            }
            catch (Exception ex)
            {
                throw context.SchemaFailure($"Error retrieving columns of table '{tableName}'. Error: {ex.Message}", ex, cancellationToken);
            }
        }

        // Called from a catch block: a failed rollback must not hide the failure being handled.
        public void Rollback() => context.TryRollback();

        public async ValueTask CommitAsync(bool async, CancellationToken cancellationToken)
        {
            if (async)
                await context.CommitAsync(cancellationToken);
            else
                context.Commit();
        }

        public void Dispose() => context.Dispose();

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
