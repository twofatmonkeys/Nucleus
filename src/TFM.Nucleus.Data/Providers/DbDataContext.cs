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
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The <see cref="IDataContext"/> every relational provider shares, over System.Data.Common:
    /// one connection, one command and an optional transaction per context; a tree of child
    /// contexts whose transactions commit or roll back with their root; a process-wide limit on
    /// open contexts; reads into <see cref="DataSet"/> with child collection result sets; deletes
    /// that report the rows they affected; and the two write paths (split by parameter count on
    /// the sync path; grouped by table, then bulk copied, fanned out or run as one command on the
    /// async path). A provider supplies the connection and overrides the hooks its engine needs.
    /// </summary>
    /// <typeparam name="TSelf">The provider's context type; each type keeps its own pool of open contexts.</typeparam>
    public abstract class DbDataContext<TSelf> : IDataContext, IDisposable, IAsyncDisposable where TSelf : DbDataContext<TSelf>
    {
        private static readonly ConcurrentDictionary<Guid, TSelf> _contextCache = new();
        private readonly ConcurrentDictionary<Guid, TSelf> _childContexts = new();
        private readonly RelationalDataProvider _provider;

        private bool _useDependency = false;
        private bool _useTransaction = false;
        private bool _disposed = false;
        private bool _needsRollback = false;
        private bool _isInitialized = false;
        private DbTransaction? _transaction;
        private DbCommand? _command;
        private TSelf? _parent;
        private DbConnection? _connection;

        /// <summary>Identifies this context in the pool and in diagnostics.</summary>
        protected Guid ContextId { get; } = Guid.NewGuid();

        protected DbDataContext(RelationalDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>The provider that created this context.</summary>
        protected RelationalDataProvider Provider => _provider;

        protected RelationalProviderOptions Options => _provider.Options;

        protected DbConnection? Connection => _connection;
        protected DbTransaction? Transaction => _transaction;
        protected DbCommand? Command => _command;

        public bool HasTransaction => _useTransaction;
        public bool HasDependency => _useDependency;
        private bool HasChildren => !_childContexts.IsEmpty;
        public int ConnectionsAvailable => Options.MaxConnectionPoolSize - _contextCache.Values.Count(x => !x._disposed);

        // ---- Hooks a provider implements or overrides.

        /// <summary>A new, closed connection; a non-transactional one must not enlist in an ambient transaction.</summary>
        protected abstract DbConnection BuildConnection(bool useTransaction);

        /// <summary>A child context on this context's transaction, for the parallel write path.</summary>
        protected abstract Task<TSelf> CreateChildContextAsync(CancellationToken token);

        /// <summary>The connection is open; start whatever the dependency option needs.</summary>
        protected virtual void OnOpened() { }

        /// <summary>The context is being disposed; stop whatever <see cref="OnOpened"/> started.</summary>
        protected virtual void OnDisposing() { }

        /// <summary>A command is about to run with its text set.</summary>
        protected virtual void PrepareCommand(DbCommand command) { }

        /// <summary>True when an opening failure is transient and worth another attempt.</summary>
        protected virtual bool IsTransientOpenFailure(Exception exception) => false;

        /// <summary>How many times opening is attempted when failures are transient.</summary>
        protected virtual int OpenAttempts => 1;

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

        /// <summary>
        /// False for an engine that lets one connection write at a time. An oversized write the
        /// bulk engine cannot take then runs as commands one after another on this context, as the
        /// sync path does, instead of fanning out to child contexts: a child's connection would wait
        /// on this context's write lock until it timed out.
        /// </summary>
        protected virtual bool AllowsConcurrentWriters => true;

        /// <summary>The connection in diagnostics.</summary>
        protected virtual string DescribeConnection() => _connection?.GetHashCode().ToString() ?? "null connection";

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

            if (async)
            {
                var sw = Stopwatch.StartNew();
                while (ConnectionsAvailable < 1)
                {
                    await Task.Delay(100);
                    if (sw.ElapsedMilliseconds > Options.CommandTimeoutSeconds * 1000)
                        throw new Exception("Connection Pool size exceeded");
                }
            }
            else if (ConnectionsAvailable < 1)
                throw new Exception("Connection Pool size exceeded");
            Debug.WriteLine($"Data Context Info: {ConnectionsAvailable} connections currently available from {_contextCache.Count} total in cache"
                , "Database");
            if (parent != null && parent.GetType() != GetType())
                throw new Exception("Parent object must be of type DataContext");

            _parent = parent;
            _useTransaction = useTransaction;
            _useDependency = useDependency;
            _connection = BuildConnection(useTransaction);
            if (!_contextCache.TryAdd(ContextId, (TSelf)this))
                throw new Exception("Error adding Context to collection.");
            try
            {
                await OpenConnection(async, token);
                _command = _connection.CreateCommand();
                if (HasTransaction)
                    _transaction = async ? await _connection.BeginTransactionAsync(token) : _connection.BeginTransaction();
                if (HasTransaction)
                    _command.Transaction = _transaction;
            }
            catch
            {
                // A context that never opened must not keep its pool slot.
                _contextCache.TryRemove(ContextId, out _);
                if (async)
                    await _connection.DisposeAsync();
                else
                    _connection.Dispose();
                _connection = null;
                throw;
            }
            TryEnlistParent(parent);
            if (_useDependency)
                OnOpened();
            Debug.WriteLine($"Created database object {ContextId} using connection {DescribeConnection()} at {DateTime.Now:H:mm:ss.ff} {(HasTransaction ? "Using a transaction." : "")}" +
                $"{(_parent != null ? $"Using parent object {_parent.ContextId} with {_parent._childContexts.Count} child conections" : "")}"
                , "Database");
            _isInitialized = true;
        }

        private ValueTask OpenConnection(bool async, CancellationToken token) =>
            ConnectionOpener.OpenAsync(_connection!, async, OpenAttempts, IsTransientOpenFailure, token);

        private void TryEnlistParent(TSelf? parent)
        {
            if (parent != null)
            {
                if (!parent.HasTransaction)
                    throw new Exception("Unable to enlist transaction from parent Database object. HasTransaction is false.");
                _parent = parent;
                _parent._childContexts.TryAdd(ContextId, (TSelf)this);
            }
        }

        #endregion

        #region IDataContext Implementation
        bool IDataContext.HasTransaction => HasTransaction;
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

        void IDataContext.Save(ICoreObject obj) => ExecuteSQL(BuildSaveQuery(obj));
        void IDataContext.Delete(ICoreObject obj) => ExecuteSQL(BuildDeleteQuery(obj));

        Task IDataContext.SaveAsync(ICoreObject obj) => ExecuteSQLAsync(BuildSaveQuery(obj), CancellationToken.None);
        Task IDataContext.SaveAsync(ICoreObject obj, CancellationToken token) => ExecuteSQLAsync(BuildSaveQuery(obj), token);
        Task IDataContext.DeleteAsync(ICoreObject obj) => ExecuteSQLAsync(BuildDeleteQuery(obj), CancellationToken.None);
        Task IDataContext.DeleteAsync(ICoreObject obj, CancellationToken token) => ExecuteSQLAsync(BuildDeleteQuery(obj), token);

        private Query BuildSaveQuery(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            return BuildWriteQuery(_provider.GetTableSchema(obj.GetType()), obj);
        }

        /// <summary>
        /// The object's insert or update, with its child collections' writes linked. The object
        /// is bound to this context's provider first, which those child writes are built through.
        /// </summary>
        private Query BuildWriteQuery(TableSchema tableSchema, ICoreObject obj)
        {
            _provider.Bind(obj);
            return obj.IsNew ? new InsertQuery(_provider.Renderer, tableSchema, obj) : new UpdateQuery(_provider.Renderer, tableSchema, obj);
        }

        /// <summary>The object's row and every row of its child collections, loaded or not.</summary>
        private DeleteWhereQuery BuildDeleteQuery(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            var tableSchema = _provider.GetTableSchema(obj.GetType());
            if (!tableSchema.ObjectSchema.IsDeletable)
                throw new NotSupportedException($"Type {tableSchema.ObjectSchema.FullName} is not [Deletable]; deletion is opt-in.");
            return _provider.CreateDeleteByIdQuery(tableSchema, [obj.ID])!;
        }

        int IDataContext.DeleteWhere<T>(Expression<Func<T, bool>> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            return ExecuteDelete(_provider.CreateDeleteWhereQuery<T>(predicate.Body));
        }

        async Task<int> IDataContext.DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            return await ExecuteDeleteAsync(_provider.CreateDeleteWhereQuery<T>(predicate.Body), token);
        }

        void IDataContext.Rollback() => Rollback();
        void IDataContext.TryRollback() => TryRollback();
        Task IDataContext.TryRollbackAsync() { TryRollback(); return Task.CompletedTask; }
        Task IDataContext.TryRollbackAsync(CancellationToken token) { TryRollback(); return Task.CompletedTask; }

        void IDataContext.BulkSave(IEnumerable<ICoreObject?>? objects)
        {
            foreach (var query in BuildBulkSaveQueries(objects))
                ExecuteSQL(query);
        }
        Task IDataContext.BulkSaveAsync(IEnumerable<ICoreObject?>? objects) => BulkSaveAsync(objects, CancellationToken.None);
        Task IDataContext.BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token) => BulkSaveAsync(objects, token);

        private async Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token)
        {
            // ExecuteSQLAsync routes large parameter counts to the provider's bulk engine.
            foreach (var query in BuildBulkSaveQueries(objects))
                await ExecuteSQLAsync(query, token);
        }

        /// <summary>One linked insert chain and one linked update chain per entity type.</summary>
        private IEnumerable<Query> BuildBulkSaveQueries(IEnumerable<ICoreObject?>? objects)
        {
            if (objects == null)
                yield break;
            foreach (var typeGroup in objects.OfType<ICoreObject>().GroupBy(o => o.GetType()))
            {
                var tableSchema = _provider.GetTableSchema(typeGroup.Key);
                foreach (var operationGroup in typeGroup.GroupBy(o => o.IsNew))
                {
                    Query? root = null;
                    foreach (var obj in operationGroup)
                    {
                        var query = BuildWriteQuery(tableSchema, obj);
                        if (root == null)
                            root = query;
                        else
                            root.LinkedQueries.Add(query);
                    }
                    if (root != null)
                        yield return root;
                }
            }
        }

        IEnumerable<T> IDataContext.Load<T>(Expression expression) => Load<T>(expression);
        public INucleusCollection<T> Load<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var query = _provider.ParseSelectExpression<T>(expression);
            return ChildGraphBinder.BuildCollection<T>(query, GetDataSet(query));
        }

        async Task<IEnumerable<T>> IDataContext.LoadAsync<T>(Expression expression) => await LoadAsync<T>(expression);
        public async Task<INucleusCollection<T>> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var query = _provider.ParseSelectExpression<T>(expression);
            return ChildGraphBinder.BuildCollection<T>(query, await GetDataSetAsync(query, CancellationToken.None));
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
        /// A transactional child leaves its connection to its root, which commits or rolls back
        /// the whole tree when it is disposed: rolled back when any context in the tree asked.
        /// </summary>
        private async ValueTask DisposeCore(bool async)
        {
            if (_useDependency && _connection != null)
                OnDisposing();
            if (HasTransaction)
            {
                if (HasChildren)
                    foreach (var db in _childContexts.Values)
                        if (db._needsRollback)
                            _needsRollback = true;
                if (_parent == null)
                    await FinallyDispose(async).ConfigureAwait(false);
            }
            else
                await FinallyDispose(async).ConfigureAwait(false);
            _disposed = true;
            Debug.WriteLine($"Diposed of database object {ContextId} at {DateTime.Now:H:mm:ss.ff}", "Database");
        }

        private async ValueTask FinallyDispose(bool async)
        {
            if (HasTransaction)
            {
                foreach (var db in _childContexts.Values)
                {
                    db._needsRollback = _needsRollback;
                    await db.FinallyDispose(async).ConfigureAwait(false);
                }
                _childContexts.Clear();

                if (_transaction != null)
                {
                    if (_needsRollback)
                    {
                        if (async)
                            await _transaction.RollbackAsync().ConfigureAwait(false);
                        else
                            _transaction.Rollback();
                        Debug.WriteLine($"Database object {ContextId} rolled back at {DateTime.Now:H:mm:ss.ff}", "Database");
                    }
                    else if (async)
                        await _transaction.CommitAsync().ConfigureAwait(false);
                    else
                        _transaction.Commit();
                    if (async)
                        await _transaction.DisposeAsync().ConfigureAwait(false);
                    else
                        _transaction.Dispose();
                    _transaction = null;
                }
            }

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
            if (!_contextCache.TryRemove(ContextId, out _))
                throw new Exception("Error removing Context from collection");
        }

        public void TryRollback() => Rollback();

        /// <summary>Marks the transaction tree for rollback; it rolls back when the root is disposed.</summary>
        public void Rollback()
        {
            _needsRollback = true;
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

        /// <summary>Sets the command's text and parameters for a run; deletes skip the provider's hook.</summary>
        private DbCommand Prepare(string commandText, IEnumerable<IQueryParameter?>? terms, bool hook = true)
        {
            var command = _command ?? throw new DataContextException("No active Command", DataContextErrorKind.CurrentCommandIsNull);
            command.CommandText = commandText;
            command.Parameters.Clear();
            if (hook)
                PrepareCommand(command);
            BindParameters(command, terms);
            return command;
        }

        /// <summary>A command of its own for a read, on this context's transaction.</summary>
        private DbCommand NewReadCommand()
        {
            if (_connection == null)
                throw new DataContextException("No active Connection", DataContextErrorKind.CurrentConnectionIsNull);
            _command = _connection.CreateCommand();
            if (HasTransaction && _transaction != null)
                _command.Transaction = _transaction;
            return _command;
        }

        /// <summary>
        /// Marks a transactional context for rollback and wraps a failure: a unique violation the
        /// provider recognises becomes a <see cref="UniqueViolationException"/>, anything else an
        /// exception carrying the message; the original is always the inner exception.
        /// </summary>
        protected Exception Failure(string message, Exception exception)
        {
            if (HasTransaction)
                Rollback();
            // Cancellation is the caller's own request, not a database failure: it propagates
            // as it was thrown, on every provider, after the transaction is marked for rollback.
            if (exception is OperationCanceledException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
            return _provider.TranslateFailure(message, exception);
        }

        /// <summary>
        /// As <see cref="Failure(string, Exception)"/>, except that a failure after
        /// <paramref name="token"/> was cancelled is the cancellation. An engine can report a
        /// command it cancelled as its own error (SqlClient raises a SqlException), so that
        /// becomes an <see cref="OperationCanceledException"/> with the engine's error inside.
        /// </summary>
        protected Exception Failure(string message, Exception exception, CancellationToken token) =>
            token.IsCancellationRequested && exception is not OperationCanceledException
                ? Failure(message, new OperationCanceledException(message, exception, token))
                : Failure(message, exception);

        #endregion

        #region Reads

        public DataSet GetDataSet(IQuery query) => GetDataSetCore(query, async: false, CancellationToken.None).GetAwaiter().GetResult();

        public Task<DataSet> GetDataSetAsync(IQuery query, CancellationToken token) => GetDataSetCore(query, async: true, token).AsTask();

        /// <summary>
        /// Reads the rows, then the total count when asked, then each child collection's result
        /// set. The connection closes with the reader, so a transactional context is finished
        /// after a read.
        /// </summary>
        private async ValueTask<DataSet> GetDataSetCore(IQuery query, bool async, CancellationToken token)
        {
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
                    ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, token)
                    : command.ExecuteReader(CommandBehavior.CloseConnection);
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
                throw Failure($"Error returning Query from DB. {ex.Message}", ex);
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
                throw Failure($"Error streaming Query from DB. {ex.Message}", ex);
            }

            await using (reader)
            {
                if (reader.HasRows)
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        ReadAnyRow(reader, row.Values, dbQuery);
                        yield return row;
                    }
                }
            }
        }

        /// <summary>
        /// Runs a provider-written read, such as a catalog query, on this context. Null when it
        /// returns no rows.
        /// </summary>
        protected internal async ValueTask<DbDataReader?> ReadStatementAsync(SchemaStatement statement, bool async, CancellationToken token)
        {
            try
            {
                var command = Prepare(statement.Text, statement.Parameters);
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
                throw Failure($"Error returning Query from DB. {ex.Message}", ex);
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
                    throw Failure($"Unable to execute delete on DB. {ex.Message}", ex);
                }
            }
            return affected;
        }

        /// <summary>
        /// A chunk's own parameters, each declared once however many statements reference it.
        /// Throws before anything runs when they exceed the per-command limit.
        /// </summary>
        private ParameterCollection DeleteParameters(Query chunk)
        {
            var parameters = chunk.Parameters;
            var count = ParameterBinding.Bind(parameters).Count();
            if (count > Options.MaxParametersPerQuery)
                throw new InvalidQueryException(
                    $"A delete of {chunk.TableName} needs {count} parameters; the limit is {Options.MaxParametersPerQuery}. Narrow the predicate.");
            return parameters;
        }

        #endregion

        #region Writes

        /// <summary>Runs a query, split into commands under the parameter limit.</summary>
        public void ExecuteSQL(IQuery query)
        {
            if (query is DeleteWhereQuery delete)
            {
                ExecuteDelete(delete);
                return;
            }
            try
            {
                foreach (var qry in query.Split(Options.MaxParametersPerQuery))
                    ExecuteWriteAsync(qry, async: false, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw Failure($"Unable to get execute SQL on DB. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Runs a query grouped by table. A group over the parameter limit goes to the provider's
        /// bulk engine when it can take it, or else fans out to child contexts on this context's
        /// transaction; any other group runs as one command.
        /// </summary>
        public async Task ExecuteSQLAsync(IQuery query, CancellationToken token)
        {
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
                    if (queriesByType.GetParameterCount() > Options.MaxParametersPerQuery && CanUseBulkPath(queriesByType))
                    {
                        await ExecuteBulkAsync(queriesByType, token);
                    }
                    else if (!AllowsConcurrentWriters && queriesByType.GetParameterCount() > Options.MaxParametersPerQuery)
                    {
                        // One writer at a time: the parts run in order on this context's connection.
                        foreach (var part in queriesByType.Split(Options.MaxParametersPerQuery))
                            await ExecuteWriteAsync(part, async: true, token);
                    }
                    else if (queriesByType.NeedsSplitting(Options.MaxParametersPerQuery, out var queries))
                    {
                        // Fallback parallel path for queries without schema
                        await Parallel.ForEachAsync(queries!,
                            new ParallelOptions { MaxDegreeOfParallelism = Options.MaxThreadsForLargeQuery },
                            async (batchQuery, ct) =>
                            {
                                await using var ctx = await CreateChildContextAsync(ct);
                                await ctx.ExecuteSQLAsync(batchQuery, ct);
                            });
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
                throw Failure($"Unable to execute SQL on DB. {ex.Message}", ex, token);
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
            try
            {
                var command = Prepare(statement.Text, statement.Parameters);
                command.CommandTimeout = Options.SchemaCommandTimeoutSeconds;
                if (async)
                    await command.ExecuteNonQueryAsync(token);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw Failure($"Unable to get execute SQL on DB. {ex.Message}", ex);
            }
        }

        #endregion

        public override string ToString()
        {
            return $"Database object {ContextId} - using Connection {DescribeConnection()}";
        }
    }

    /// <summary>A failure of a data context itself, as opposed to the database.</summary>
    public class DataContextException : Exception
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
        Unspecified = 99,
    }

    /// <summary>
    /// The transaction a table rebuild runs in: one transactional context, which commits when
    /// disposed unless rolled back. Columns are read through the transaction, so a renamed table
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
                throw new Exception($"Error retrieving columns of table '{tableName}'. Error: {ex.Message}", ex);
            }
        }

        public void Rollback() => context.Rollback();

        public void Dispose() => context.Dispose();

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
