global using TFM.Nucleus.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus;


using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    public partial class SqlDataContext : IDataContext, IDisposable, IAsyncDisposable
    {
        private readonly IOptionsMonitor<SQLConfiguration> _SqlConfigOptions;
        private readonly IOptionsMonitor<NucleusOptions> _NucleusOptions;
        private SQLConfiguration SqlConfig => _SqlConfigOptions.CurrentValue;
        private NucleusOptions NucleusOptions => _NucleusOptions.CurrentValue;

        private bool _UseDependency  = false;
        private bool _UseTransaction = false;
        private bool _Disposed = false;
        private bool _NeedsRollback = false;
        private bool _IsInitialized = false;
        private readonly Guid _ID = Guid.NewGuid();
        private SqlTransaction? _CurrentTransaction;
        private SqlCommand? _CurrentCommand;
        private SqlDataContext? _ParentDataContext;
        private SqlConnection? _CurrentConnection;
        private static readonly ConcurrentDictionary<Guid,SqlDataContext> _ContextCache = new ConcurrentDictionary<Guid, SqlDataContext>();
        private readonly ConcurrentDictionary<Guid, SqlDataContext> _ChildContexts = new ConcurrentDictionary<Guid, SqlDataContext>();

        public delegate void NewMessage();
        public event NewMessage? OnNewMessage;
        
        private bool HasChildren => !_ChildContexts.IsEmpty;
        
        public bool HasTransaction => _UseTransaction;
        public bool HasDependency => _UseDependency;
        public int ConnectionsAvailable => SqlConfig.MaxConnectionPoolSize - _ContextCache.Values.Where(x => !x._Disposed).Count();
        private SQLDataProvider? _Provider;

        /// <summary>
        /// The provider that created this context; required for the object-level Save/Delete/Load
        /// convenience surface (query construction needs table schemas).
        /// </summary>
        private SQLDataProvider Provider => _Provider
            ?? throw new SqlDataContextException("This context was created without a provider reference.", SqlDataContextExceptionType.CurrentConnectionIsNull);

        #region Constructors

        //public static Task<SqlDataContext> InitializeAsync(SqlDataContext parent) =>
        //    InitializeAsync(parent?.HasTransaction ?? false, false, parent, parent!._SqlConfigOptions, CancellationToken.None);

        //public static async Task<SqlDataContext> InitializeAsync(bool usetransaction, bool usedependency, SqlDataContext? parent, IOptions<SQLConfiguration> config, CancellationToken token)
        //{
            
        //    var db = new SqlDataContext(config);
        //    if (!_ContextCache.TryAdd(db._ID, db))
        //        throw new Exception("Error adding Context to collection.");
        //    if (db.ConnectionsAvailable < 1)
        //        throw new Exception("Connection Pool size exceeded");
        //    Debug.WriteLine($"{db.ConnectionsAvailable} connections currently available"
        //        , "Database");
        //    db._UseTransaction = usetransaction;
        //    db._UseDependency = usedependency;

        //    db._CurrentConnection = db.BuildNewSQLConnection();
        //    await db._CurrentConnection.OpenAsync(token);
        //    if (db.HasTransaction)
        //        db._CurrentTransaction = (SqlTransaction)await db._CurrentConnection.BeginTransactionAsync(token);
        //    if (parent != null)
        //    {
        //        if (!parent.HasTransaction)
        //            throw new Exception("Unable to enlist transaction from parent Database object. UseTransaction is false.");
        //        db._ParentDataContext = parent;
        //        db._ParentDataContext._AddChild(db);

        //    }

        //    if (db._UseDependency)
        //    {

        //        // Start the dependency
        //        // User must have SUBSCRIBE QUERY NOTIFICATIONS permission
        //        // Database must also have SSB enabled
        //        // ALTER DATABASE Chatter SET ENABLE_BROKER
        //        SqlDependency.Start(db._CurrentConnection.ConnectionString);
        //    }

        //    Debug.WriteLine($"Created database object async {db._ID} using connection {db._CurrentConnection.ClientConnectionId} at {DateTime.Now.ToString("H:mm:ss.ff")} {(usetransaction ? "Using a transaction." : "")}" +
        //        $"{(parent != null ? $"Using parent object {parent._ID} with {parent._ChildContexts.Count} child conections" : "")}"
        //        , "Database");
        //    return db;
        //}

        internal static SqlDataContext CreateContext(SqlDataContext? parent, bool useTransaction,
            bool useDependency, IOptionsMonitor<SQLConfiguration> config, IOptionsMonitor<NucleusOptions> nucleusOptions,
            SQLDataProvider? provider = null)
        {
            var sqlContext = new SqlDataContext(config, nucleusOptions) { _Provider = provider };
            sqlContext.ConstructContext(parent, useTransaction, useDependency);
            sqlContext._CurrentConnection!.Open();
            sqlContext._CurrentCommand = sqlContext._CurrentConnection.CreateCommand();
            if (sqlContext.HasTransaction)
                sqlContext._CurrentTransaction = (SqlTransaction)sqlContext._CurrentConnection.BeginTransaction();
            sqlContext._CurrentCommand.Connection = sqlContext._CurrentConnection;
            if (sqlContext.HasTransaction)
                sqlContext._CurrentCommand.Transaction = sqlContext._CurrentTransaction;
            sqlContext.TryEnlistParent(parent);
            sqlContext.TryStartDependency();
            sqlContext.LogCreation();
            sqlContext._IsInitialized = true;
            return sqlContext;
        }


        internal static async Task<SqlDataContext> CreateContextAsync(SqlDataContext? parent, bool useTransaction,
            bool useDependency, CancellationToken token, IOptionsMonitor<SQLConfiguration> config, IOptionsMonitor<NucleusOptions> nucleusOptions,
            SQLDataProvider? provider = null)
        {
            var sqlContext = new SqlDataContext(config, nucleusOptions) { _Provider = provider };
            await sqlContext.ConstructContextAsync(parent, useTransaction, useDependency);
            await sqlContext._CurrentConnection!.OpenAsync(token);
            sqlContext._CurrentCommand = sqlContext._CurrentConnection.CreateCommand();
            if (sqlContext.HasTransaction)
                sqlContext._CurrentTransaction = (SqlTransaction)await sqlContext._CurrentConnection.BeginTransactionAsync(token);
            if (sqlContext.HasTransaction)
                sqlContext._CurrentCommand.Transaction = sqlContext._CurrentTransaction;
            sqlContext.TryEnlistParent(parent);
            sqlContext.TryStartDependency();
            sqlContext.LogCreation();
            sqlContext._IsInitialized = true;
            return sqlContext;
        }

        private SqlDataContext(IOptionsMonitor<SQLConfiguration> config, IOptionsMonitor<NucleusOptions> nucleusOptions)
        {
            _SqlConfigOptions = config ?? throw new ArgumentNullException(nameof(config));
            _NucleusOptions = nucleusOptions ?? throw new ArgumentNullException(nameof(nucleusOptions));
            if (_SqlConfigOptions.CurrentValue == null)
                throw new ArgumentNullException(nameof(config.CurrentValue));
        }

        private void ConstructContext(SqlDataContext? parent, bool useTransaction, bool useDependency)
        {
            if(_IsInitialized)
                throw new Exception("Context has already been initialized");

            if (ConnectionsAvailable < 1)
                throw new Exception("Connection Pool size exceeded");
            Debug.WriteLine($"Data Context Info: {ConnectionsAvailable} connections currently available from {_ContextCache.Count} total in cache"
                , "Database");
            if (parent != null)
                if (parent.GetType() != typeof(SqlDataContext))
                    throw new Exception("Parent object must be of type DataContext");


            _ParentDataContext = parent;
            _UseTransaction = useTransaction;
            _UseDependency = useDependency;
            _CurrentConnection = SqlConfig.BuildNewSQLConnection(useTransaction);
            if (!_ContextCache.TryAdd(_ID, this))
                throw new Exception("Error adding Context to collection.");
        }
        private async Task ConstructContextAsync(SqlDataContext? parent, bool useTransaction, bool useDependency)
        {
            if (_IsInitialized)
                throw new Exception("Context has already been initialized");

            var sw = Stopwatch.StartNew();
            while (ConnectionsAvailable < 1)
            {
                await Task.Delay(100);
                if (sw.ElapsedMilliseconds > SqlConfig.CommandTimeoutSeconds * 1000)
                    throw new Exception("Connection Pool size exceeded");
            }
            Debug.WriteLine($"Data Context Info: {ConnectionsAvailable} connections currently available from {_ContextCache.Count} total in cache"
                , "Database");
            if (parent != null)
                if (parent.GetType() != typeof(SqlDataContext))
                    throw new Exception("Parent object must be of type DataContext");


            _ParentDataContext = parent;
            _UseTransaction = useTransaction;
            _UseDependency = useDependency;
            _CurrentConnection = SqlConfig.BuildNewSQLConnection(useTransaction);
            if (!_ContextCache.TryAdd(_ID, this))
                throw new Exception("Error adding Context to collection.");
        }
        private void TryEnlistParent(SqlDataContext? parent)
        {
            if (parent != null)
            {
                if (!parent.HasTransaction)
                    throw new Exception("Unable to enlist transaction from parent Database object. HasTransaction is false.");
                _ParentDataContext = (SqlDataContext?)parent;
                _ParentDataContext!._AddChild(this);
            }
        }
        private void TryStartDependency()
        {
            if (_UseDependency)
            {
                // Start the dependency
                // User must have SUBSCRIBE QUERY NOTIFICATIONS permission
                // Database must also have SSB enabled
                // ALTER DATABASE Chatter SET ENABLE_BROKER
                SqlDependency.Start(_CurrentConnection!.ConnectionString);
            }
        }
        private void LogCreation()
        {
            Debug.WriteLine($"Created database object {_ID} using connection {_CurrentConnection!.ClientConnectionId} at {DateTime.Now.ToString("H:mm:ss.ff")} {(HasTransaction ? "Using a transaction." : "")}" +
                $"{(_ParentDataContext != null ? $"Using parent object {_ParentDataContext._ID} with {_ParentDataContext._ChildContexts.Count} child conections" : "")}"
                , "Database");
        }
        #endregion

        #region IDataContext Implementation
        bool IDataContext.HasTransaction => HasTransaction;
        bool IDataContext.HasDependency => _UseDependency;
        bool IDataContext.HasChildren => HasChildren;
        IDataContext? IDataContext.Parent => _ParentDataContext;
        
        DataSet IDataContext.GetDataSet(IQuery query) => GetDataSet(query);
        Task<DataSet> IDataContext.GetDataSetAsync(IQuery query) => GetDataSetAsync((Query)query, CancellationToken.None);
        Task<DataSet> IDataContext.GetDataSetAsync(IQuery query, CancellationToken token) => GetDataSetAsync((Query)query, token);
        IAsyncEnumerable<StreamingRow> IDataContext.StreamAsync(IQuery query, CancellationToken ct) => StreamAsync(query, ct);

        void IDataContext.ExecuteQuery(IQuery query) => ExecuteSQL(query);
        Task IDataContext.ExecuteQueryAsync(IQuery query) => ExecuteSQLAsync(query, null);
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

        Task IDataContext.SaveAsync(ICoreObject obj) => ExecuteSQLAsync(BuildSaveQuery(obj), null);
        Task IDataContext.SaveAsync(ICoreObject obj, CancellationToken token) => ExecuteSQLAsync(BuildSaveQuery(obj), token);
        Task IDataContext.DeleteAsync(ICoreObject obj) => ExecuteSQLAsync(BuildDeleteQuery(obj), null);
        Task IDataContext.DeleteAsync(ICoreObject obj, CancellationToken token) => ExecuteSQLAsync(BuildDeleteQuery(obj), token);

        private Query BuildSaveQuery(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            var tableSchema = Provider.GetTableSchema(obj.GetType());
            return obj.IsNew ? new InsertQuery(SqlServerQueryRenderer.Instance, tableSchema, obj) : new UpdateQuery(SqlServerQueryRenderer.Instance, tableSchema, obj);
        }

        /// <summary>The object's row and every row of its child collections, loaded or not.</summary>
        private DeleteWhereQuery BuildDeleteQuery(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            var tableSchema = Provider.GetTableSchema(obj.GetType());
            if (!tableSchema.ObjectSchema.IsDeletable)
                throw new NotSupportedException($"Type {tableSchema.ObjectSchema.FullName} is not [Deletable]; deletion is opt-in.");
            return Provider.CreateDeleteByIdQuery(tableSchema, [obj.ID])!;
        }

        int IDataContext.DeleteWhere<T>(Expression<Func<T, bool>> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            return ExecuteDelete(Provider.CreateDeleteWhereQuery<T>(predicate.Body));
        }

        async Task<int> IDataContext.DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            if (!ExpressionParser.CheckDeletePredicate(predicate.Body))
                return 0;
            return await ExecuteDeleteAsync(Provider.CreateDeleteWhereQuery<T>(predicate.Body), token);
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
            // ExecuteSQLAsync routes large parameter counts to the bulk (#temp + SqlBulkCopy) engine.
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
                var tableSchema = Provider.GetTableSchema(typeGroup.Key);
                foreach (var operationGroup in typeGroup.GroupBy(o => o.IsNew))
                {
                    Query? root = null;
                    foreach (var obj in operationGroup)
                    {
                        Query query = operationGroup.Key ? new InsertQuery(SqlServerQueryRenderer.Instance, tableSchema, obj) : new UpdateQuery(SqlServerQueryRenderer.Instance, tableSchema, obj);
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
        #endregion

        private void _AddChild(SqlDataContext childContext) => _ChildContexts.TryAdd(childContext._ID, childContext);
        private void RemoveChild(SqlDataContext childContext)
        {
            if(_ChildContexts.TryRemove(childContext._ID, out var childDB))
                childDB?.Dispose();
        }
        private async ValueTask RemoveChildAsync(SqlDataContext db)
        {
            _ChildContexts.TryRemove(db._ID, out var childDB);
                if(childDB != null)
                    await childDB.DisposeAsync();
        }
        public void Dispose()
        {
            Debug.WriteLine($"Attempting to dispose of database object {_ID} at {DateTime.Now.ToString("H:mm:ss.ff")}{(HasChildren?" with "+_ChildContexts.Values.Count+" child db's":"")}", "Database");
            if (_Disposed)
                return;
            _CurrentCommand?.Dispose();
            _CurrentCommand = null;
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_UseDependency && _CurrentConnection != null)
                    SqlDependency.Stop(_CurrentConnection.ConnectionString);
                if (HasTransaction)
                {
                    if (HasChildren)
                        foreach (var db in _ChildContexts.Values)
                            if (db._NeedsRollback)
                                _NeedsRollback = true;
                    if (_ParentDataContext == null)
                        FinallyDispose();
                }
                else
                    FinallyDispose();
            }
            _Disposed = true;
            Debug.WriteLine($"Diposed of database object {_ID} at {DateTime.Now.ToString("H:mm:ss.ff")}", "Database");
        }

        public async ValueTask DisposeAsync()
        {
            if (_Disposed)
                return;
            if(_CurrentCommand != null)
                await _CurrentCommand.DisposeAsync().ConfigureAwait(false);
            _CurrentCommand = null;
            await DisposeAsyncCore().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        //protected virtual async ValueTask DisposeAsyncCore()
        //{
        //    if (_Disposed)
        //        return;
        //    if (_UseDependency)
        //        SqlDependency.Stop(_CurrentConnection?.ConnectionString);
        //    if (HasTransaction)
        //    {
        //        if (HasChildren)
        //            foreach (var db in _ChildContexts.Values)
        //                if (db._NeedsRollback)
        //                    _NeedsRollback = true;
        //        if (_ParentDataContext == null)
        //            await FinallyDisposeAsync().ConfigureAwait(false);
        //    }
        //    else
        //        await FinallyDisposeAsync().ConfigureAwait(false);
        //}
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_Disposed)
                return;
            if (_UseDependency)
                SqlDependency.Stop(_CurrentConnection?.ConnectionString);
            if (HasTransaction)
            {
                if (HasChildren)
                    foreach (var db in _ChildContexts.Values)
                        if (db._NeedsRollback)
                            _NeedsRollback = true;
                if (_ParentDataContext == null)
                    await FinallyDisposeAsync().ConfigureAwait(false);
            }
            else
                await FinallyDisposeAsync().ConfigureAwait(false);

            _Disposed = true;
        }
        private void FinallyDispose()
        {
            if (HasTransaction)
            {
                foreach (var db in _ChildContexts.Values)
                {
                    db._NeedsRollback = this._NeedsRollback;
                    db.FinallyDispose();
                    db.RemoveChild(db);
                }

                if (_NeedsRollback)
                {
                    _CurrentTransaction?.Rollback();
                    Debug.WriteLine($"Database object {_ID} rolled back at {DateTime.Now.ToString("H:mm:ss.ff")}", "Database");
                }
                else
                {
                    _CurrentTransaction?.Commit();
                }
                _CurrentTransaction?.Dispose();
                _CurrentTransaction = null;
            }

            _CurrentConnection?.Close();
            Debug.WriteLine($"Disposing of database connection {_CurrentConnection?.ClientConnectionId} at {DateTime.Now.ToString("H:mm:ss.ff")}", "Database");
            _CurrentConnection?.Dispose();
            _CurrentConnection = null;
            if (!_ContextCache.Remove(_ID, out _))
                throw new Exception("Error removing Context from collection");
        }
        private async ValueTask FinallyDisposeAsync()
        {
            if (HasTransaction)
            {
                foreach (var db in _ChildContexts.Values)
                {
                    db._NeedsRollback = this._NeedsRollback;
                    await db.FinallyDisposeAsync().ConfigureAwait(false);
                    await db.RemoveChildAsync(db).ConfigureAwait(false);
                }

                if (_CurrentTransaction != null)
                {
                    if (_NeedsRollback)
                        await _CurrentTransaction.RollbackAsync();
                    else
                        await _CurrentTransaction.CommitAsync();
                    
                    await _CurrentTransaction.DisposeAsync();
                    _CurrentTransaction = null;
                }
            }
            if (_CurrentConnection == null)
                return;
            await _CurrentConnection.CloseAsync();
            Debug.WriteLine($"Disposing async database connection {_CurrentConnection.ClientConnectionId} at {DateTime.Now.ToString("H:mm:ss.ff")}", "Database");
            await _CurrentConnection.DisposeAsync();
            _CurrentConnection = null;
        }
        public void TryRollback() => Rollback();

        public void Rollback()
        {
            _NeedsRollback = true;
            //_CurrentTxnScope.Dispose();
            //this.Dispose();
        }

        private void _CreateNewCommand()
        {
            if (_CurrentCommand != null)
                throw new SqlDataContextException("An active Command already exists", SqlDataContextExceptionType.UnableToCreateNewCommand);
            if(_CurrentConnection == null)
                throw new SqlDataContextException("No active Connection", SqlDataContextExceptionType.CurrentConnectionIsNull);
            _CurrentCommand = _CurrentConnection.CreateCommand();
            if (HasTransaction && _CurrentTransaction != null)
                _CurrentCommand.Transaction = _CurrentTransaction;
        }


        IEnumerable<T> IDataContext.Load<T>(Expression expression) => Load<T>(expression);
        public INucleusCollection<T> Load<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var query = Provider.ParseSelectExpression<T>(expression);
            return ChildGraphBinder.BuildCollection<T>(query, GetDataSet(query));
        }

        async Task<IEnumerable<T>> IDataContext.LoadAsync<T>(Expression expression) => await LoadAsync<T>(expression);
        public async Task<INucleusCollection<T>> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new()
        {
            if (ExpressionParser.Classify(expression) == PredicateShape.MatchesNone)
                return new NucleusCollection<T>();
            var query = Provider.ParseSelectExpression<T>(expression);
            return ChildGraphBinder.BuildCollection<T>(query, await GetDataSetAsync(query, CancellationToken.None));
        }


        public Task<DbDataReader?> GetObjectDataAsync(IQuery query) =>
            _GetObjectDataAsync(query.ToString()!, query.GetSQLParams(), CancellationToken.None);
        public Task<DbDataReader?> GetObjectDataAsync(IQuery query, CancellationToken token) =>
            _GetObjectDataAsync(query.ToString()!, query.GetSQLParams(), token);

        /// <summary>Reads a provider-written statement, such as a catalog query, on this context.</summary>
        internal Task<DbDataReader?> GetObjectDataAsync(string commandText) =>
            _GetObjectDataAsync(commandText, null, CancellationToken.None);
        internal Task<DbDataReader?> GetObjectDataAsync(string commandText, CancellationToken token) =>
            _GetObjectDataAsync(commandText, null, token);

        private async Task<DbDataReader?> _GetObjectDataAsync(string commandText, SqlParameter[]? parameters, CancellationToken token)
        {
            try
            {
                _CurrentCommand!.CommandText = commandText;
                _CurrentCommand.Parameters.Clear();
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new SqlDependency(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(parameters);
                var reader = await _CurrentCommand.ExecuteReaderAsync(CommandBehavior.Default, token);
                if (reader == null || !reader.HasRows)
                {
                    if(reader != null)
                        await reader.DisposeAsync();
                    return null;
                }
                else
                {
                    //_CurrentCommand.StatementCompleted += _CurrentCommand_StatementCompleted;
                    return reader;
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }

        private void _CurrentCommand_StatementCompleted(object sender, StatementCompletedEventArgs e)
        {
            _CurrentCommand?.Dispose();
        }

        public Task SetObjectDataAsync(IQuery query, CancellationToken token) =>
            _SetObjectDataAsync(query, token);
        private async Task _SetObjectDataAsync(IQuery query, CancellationToken token)
        {
            try
            {
                if (query.NeedsSplitting(2000, out var splitQueries))
                {
                    var maxQueryThreads = Math.Min(SqlConfig.MaxThreadsForLargeQuery, ConnectionsAvailable / 2);
                    var contextQueue = new ConcurrentQueue<SqlDataContext?>();
                    for (int i = 0; i < maxQueryThreads; i++)
                        contextQueue.Enqueue(null);
                    var tokenSource = new CancellationTokenSource();
                    token.Register(() => tokenSource.Cancel());
                    var tasks = splitQueries!.Select(async qry =>
                    {
                        try
                        {
                            SqlDataContext? freeContext = null;
                            while (freeContext == null)
                            {
                                if (contextQueue.TryDequeue(out var context))
                                    freeContext = context ?? await CreateContextAsync(this, HasTransaction, HasDependency,token, _SqlConfigOptions, _NucleusOptions) as SqlDataContext;
                                else
                                    await Task.Delay(10);
                                tokenSource.Token.ThrowIfCancellationRequested();
                            }
                            await freeContext._SetObjectDataAsync(qry, tokenSource.Token);
                            contextQueue.Enqueue(freeContext);
                        }
                        catch
                        {
                            tokenSource.Cancel();
                            throw;
                        }
                    });
                    await Task.WhenAll(tasks);
                    while (!contextQueue.IsEmpty)
                        if (contextQueue.TryDequeue(out var usedContext))
                            if(usedContext != null)
                                await usedContext.DisposeAsync();
                }
                else
                {
                    _CreateNewCommand();
                    _CurrentCommand!.CommandText = query.ToString();
                    if (_UseDependency)
                    {
                        _CurrentCommand.Notification = null;
                        SqlDependency dependency = new SqlDependency(_CurrentCommand);
                        dependency.OnChange += new OnChangeEventHandler(OnChange);
                    }
                    _CurrentCommand.Parameters.TryAddRange(query.AllQueryParameters.GetSQLParams());
                    var _outputDictionary = new Dictionary<string, IQueryOutputParameter>();
                    if(query.AllQueryOutputParameters != null && query.AllQueryOutputParameters.Any())
                        foreach (var op in query.AllQueryOutputParameters)
                            _outputDictionary.Add(op!.Variable!, (IQueryOutputParameter)op);
                    using var reader = await _CurrentCommand.ExecuteReaderAsync(CommandBehavior.Default, token);
                    if (reader != null && reader.HasRows)
                    {
                        do
                        {
                            if (await reader.ReadAsync(token))
                            {
                                int i = 0;
                                foreach (var col in reader.GetColumnSchema().Select(x => x.ColumnName))
                                {
                                    _outputDictionary[col].ReceivedObject = reader.GetValue(i);
                                    i++;
                                }
                            }
                        }
                        while (await reader.NextResultAsync(token)) ;
                    }
                    await _CurrentCommand.DisposeAsync();
                    _CurrentCommand = null;
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }


        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        







        
        
        
        
        
        
        
        
        
        public DataTable RunQuery(string query) => _RunQuery(query, null);
        public DataTable RunQuery(string query, IQueryParameter? parameter) => _RunQuery(query, parameter?.CreateCollection());
        public DataTable RunQuery(string query, IQueryParameterCollection? parameters) => _RunQuery(query, parameters);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "<Pending>")]
        private DataTable _RunQuery(string query, IQueryParameterCollection? parameters)
        {
            try
            {
                _CurrentCommand!.CommandText = query;
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new SqlDependency(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(parameters.GetSQLParams());
                using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
                {
                    var DT = new DataTable();
                    SQLDA.Fill(DT);
                    return DT;
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }




        public Task<DataTable> RunQueryAsync(string query) => _RunQueryAsync(query, null);
        //public Task<DataTable> RunQueryAsync(string query, Parameter parameter) => _RunQueryAsync(query,parameter?.CreateCollection());
        public Task<DataTable> RunQueryAsync(string query, IQueryParameterCollection? parameters) => _RunQueryAsync(query, parameters);
        private async Task<DataTable> _RunQueryAsync(string query, IQueryParameterCollection? parameters)
        {
            try
            {
                _CurrentCommand = _CurrentConnection!.CreateCommand();
                if(HasTransaction && _CurrentTransaction != null)
                    _CurrentCommand.Transaction = _CurrentTransaction;
                _CurrentCommand.CommandText = query;
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new SqlDependency(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
                var DT = new DataTable();
                try
                {

                    await Task.Run(() =>
                    {
                        using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
                            SQLDA.Fill(DT);
                    });
                    await _CurrentCommand.DisposeAsync();
                    return DT;
                }
                catch (TargetInvocationException tEx)
                {
                    if(tEx.InnerException != null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tEx.InnerException).Throw();
                    throw;
                }
                
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }


        // [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "<Pending>")]
        //public DataSet RunAggregrateQuery(string query, IQueryParameterCollection parameters)
        //{
        //    try
        //    {
        //        _CurrentCommand.CommandText = query;
        //        if (_UseDependency)
        //        {
        //            _CurrentCommand.Notification = null;
        //            SqlDependency dependency = new SqlDependency(_CurrentCommand);
        //            dependency.OnChange += new OnChangeEventHandler(OnChange);
        //        }
        //        _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
        //        using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
        //        {
        //            var DS = new DataSet();
        //            SQLDA.Fill(DS);
        //            return DS;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        if (UseTransaction)
        //            Rollback();
        //        throw new Exception($"Unable to get data from DB. {ex.Message}", ex);
        //    }

        //}
        
        
        
        
        //public async Task<TFMDataSet[]> RunAggregrateQueryAsync(Query query, CancellationToken token)
        //{
        //    try
        //    {
        //        _CurrentCommand.CommandText = query;
        //        if (UseDependency)
        //        {
        //            _CurrentCommand.Notification = null;
        //            SqlDependency dependency = new SqlDependency(_CurrentCommand);
        //            dependency.OnChange += new OnChangeEventHandler(OnChange);
        //        }
        //        _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
        //        var DS = new DataSet();
        //            await Task.Run(() =>
        //            {
        //                using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
        //                    SQLDA.Fill(DS);
        //            });
        //            return DS;
        //    }
        //    catch (Exception ex)
        //    {
        //        if (UseTransaction)
        //            Rollback();
        //        throw new Exception($"Unable to get data from DB. {ex.Message}", ex);
        //    }

        //}
        //public async Task<DataSet> OldRunAggregrateQueryAsync(string query, IQueryParameterCollection parameters)
        //{
        //    try
        //    {
        //        _CurrentCommand.CommandText = query;
        //        if (_UseDependency)
        //        {
        //            _CurrentCommand.Notification = null;
        //            SqlDependency dependency = new SqlDependency(_CurrentCommand);
        //            dependency.OnChange += new OnChangeEventHandler(OnChange);
        //        }
        //        _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
        //        var DS = new DataSet();
        //        try
        //        {
        //            await Task.Run(() =>
        //            {
        //                using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
        //                    SQLDA.Fill(DS);
        //            });
        //            return DS;
        //        }
        //        catch (TargetInvocationException tEx)
        //        {
        //            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tEx.InnerException).Throw();
        //            throw;
        //        }

        //    }
        //    catch (Exception ex)
        //    {
        //        if (UseTransaction)
        //            Rollback();
        //        throw new Exception($"Unable to get data from DB. {ex.Message}", ex);
        //    }

        //}





        /// <summary>
        /// Runs a delete batch and returns the rows of its root type affected, summed over its
        /// chunks. Each chunk is one command, never regrouped, split or bulk copied: its child
        /// statements select through the parent's condition and must run in the order rendered.
        /// </summary>
        public int ExecuteDelete(DeleteWhereQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            var affected = 0;
            foreach (var chunk in query.FlattenLinkedQueries())
            {
                var parameters = DeleteParameters(chunk);
                try
                {
                    _CurrentCommand!.CommandText = chunk.BuildQuery();
                    _CurrentCommand.Parameters.Clear();
                    _CurrentCommand.Parameters.TryAddRange(parameters);
                    affected += Convert.ToInt32(_CurrentCommand.ExecuteScalar());
                }
                catch (Exception ex)
                {
                    if (HasTransaction)
                        Rollback();
                    throw new Exception($"Unable to execute delete on DB. {ex.Message}", ex);
                }
            }
            return affected;
        }

        /// <inheritdoc cref="ExecuteDelete(DeleteWhereQuery)"/>
        public async Task<int> ExecuteDeleteAsync(DeleteWhereQuery query, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(query);
            var affected = 0;
            foreach (var chunk in query.FlattenLinkedQueries())
            {
                var parameters = DeleteParameters(chunk);
                try
                {
                    _CurrentCommand!.CommandText = chunk.BuildQuery();
                    _CurrentCommand.Parameters.Clear();
                    _CurrentCommand.Parameters.TryAddRange(parameters);
                    affected += Convert.ToInt32(await _CurrentCommand.ExecuteScalarAsync(token));
                }
                catch (Exception ex)
                {
                    if (HasTransaction)
                        Rollback();
                    throw new Exception($"Unable to execute delete on DB. {ex.Message}", ex);
                }
            }
            return affected;
        }

        /// <summary>
        /// A chunk's own parameters, each declared once however many statements reference it.
        /// Throws before anything runs when they exceed the per-command limit.
        /// </summary>
        private SqlParameter[] DeleteParameters(Query chunk)
        {
            var parameters = chunk.Parameters.GetSQLParams() ?? [];
            if (parameters.Length > SqlConfig.MaxParametersPerQuery)
                throw new InvalidQueryException(
                    $"A delete of {chunk.TableName} needs {parameters.Length} parameters; the limit is {SqlConfig.MaxParametersPerQuery}. Narrow the predicate.");
            return parameters;
        }

        public void ExecuteSQL(IQuery query)
        {
            if (query is DeleteWhereQuery delete)
            {
                ExecuteDelete(delete);
                return;
            }
            try
            {
                foreach (var qry in query.Split(SqlConfig.MaxParametersPerQuery))
                {
                    _CurrentCommand!.CommandText = qry.ToString();
                    if (_UseDependency)
                    {
                        _CurrentCommand.Notification = null;
                        SqlDependency dependency = new SqlDependency(_CurrentCommand);
                        dependency.OnChange += new OnChangeEventHandler(OnChange);
                    }
                    _CurrentCommand.Parameters.Clear();
                    _CurrentCommand.Parameters.TryAddRange(qry.AllQueryParameters.GetSQLParams());
                    _CurrentCommand.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Unable to get execute SQL on DB. {ex.Message}", ex);
            }
        }


        public void ExecuteSQL(string query) => ExecuteSQL(query, []);
        public void ExecuteSQL(string query, Parameter? parameter) => ExecuteSQL(query, parameter?.CreateCollection());
        public void ExecuteSQL(string query, ParameterCollection? parameters)
        {
            try
            {
                _CurrentCommand!.CommandText = query;
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new SqlDependency(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
                _CurrentCommand.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Unable to get execute SQL on DB. {ex.Message}", ex);
            }
        }

        public Task ExecuteSQLAsync(string query) => ExecuteSQLAsync(query, null, null);
        public Task ExecuteSQLAsync(string query, CancellationToken? token) => ExecuteSQLAsync(query, null, token);
        public Task ExecuteSQLAsync(string query, Parameter? parameter) => ExecuteSQLAsync(query, parameter?.CreateCollection());
        public Task ExecuteSQLAsync(string query, ParameterCollection? parameters) => ExecuteSQLAsync(query, parameters, null);
        public async Task ExecuteSQLAsync(string query, ParameterCollection? parameters, CancellationToken? token)
        {
            try
            {
                _CurrentCommand!.CommandText = query;
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new SqlDependency(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
                await _CurrentCommand.ExecuteNonQueryAsync(token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Unable to get execute SQL on DB. {ex.Message}", ex);
            }
        }


        //public async Task ExecuteSQLAsync(IQuery query, CancellationToken? token)
        //{
        //    if (token == null)
        //        token = CancellationToken.None;
        //    try
        //    {
        //        if (query.NeedsSplitting(SqlConfig.MaxParametersPerQuery, out var queries))
        //        {
        //            divide queries into multiples of maxthreads
        //            await Parallel.ForEachAsync(queries!, new ParallelOptions { MaxDegreeOfParallelism = SqlConfig.MaxThreadsForLargeQuery }, async (batchQuery, token) =>
        //            {
        //                using (var ctx = await CreateContextAsync(this, HasTransaction, HasDependency, token, _SqlConfigOptions, _NucleusOptions) as SqlDataContext)
        //                {
        //                    await ctx!.ExecuteSQLAsync(batchQuery, token);
        //                }
        //            });
        //        }
        //        else
        //        {
        //            var timer = Stopwatch.StartNew();
        //            var executionId = Guid.NewGuid();
        //            Debug.WriteLine($"{DateTime.Now} Starting SQL execution {executionId} on context:{this._ID}");
        //            _CurrentCommand!.CommandText = query.ToString();
        //            if (_UseDependency)
        //            {
        //                _CurrentCommand.Notification = null;
        //                SqlDependency dependency = new SqlDependency(_CurrentCommand);
        //                dependency.OnChange += new OnChangeEventHandler(OnChange);
        //            }
        //            _CurrentCommand.Parameters.Clear();
        //            _CurrentCommand.Parameters.TryAddRange(query.AllQueryParameters.GetSQLParams());
        //            await _CurrentCommand.ExecuteNonQueryAsync();
        //            Debug.WriteLine($"{DateTime.Now} Finished SQL execution {executionId} in {timer.ElapsedMilliseconds} ms");
        //            timer.Stop();
        //            timer = null;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        if (HasTransaction)
        //            Rollback();
        //        throw new Exception($"Unable to get execute SQL on DB. {ex.Message}", ex);
        //    }
        //}
        public async Task ExecuteSQLAsync(IQuery query, CancellationToken? token)
        {
            if (token == null)
                token = CancellationToken.None;
            if (query is DeleteWhereQuery delete)
            {
                await ExecuteDeleteAsync(delete, token.Value);
                return;
            }
            try
            {
                if(!(query is Query sqlQuery))
                    throw new Exception("Only Query objects are supported for execution");

                //Split queries by type and size to determine execution path
                foreach (var queriesByType in sqlQuery.GroupByTableSchema())
                {
                    // Bulk path: single connection, single transaction, no parameter limit.
                    // Must check BEFORE NeedsSplitting which mutates LinkedQueries.
                    if (queriesByType.GetParameterCount() > SqlConfig.MaxParametersPerQuery && CanUseBulkPath(queriesByType))
                    {
                        await ExecuteBulkAsync(queriesByType, token.Value);
                    }
                    else if (queriesByType.NeedsSplitting(SqlConfig.MaxParametersPerQuery, out var queries))
                    {
                        // Fallback parallel path for queries without schema
                        await Parallel.ForEachAsync(queries!,
                            new ParallelOptions { MaxDegreeOfParallelism = SqlConfig.MaxThreadsForLargeQuery },
                            async (batchQuery, ct) =>
                            {
                                await using var ctx = await CreateContextAsync(
                                    this, HasTransaction, HasDependency, ct,
                                    _SqlConfigOptions, _NucleusOptions) as SqlDataContext;
                                await ctx!.ExecuteSQLAsync(batchQuery, ct);
                            });
                    }
                    else
                    {
                        var timer = Stopwatch.StartNew();
                        var executionId = Guid.NewGuid();
                        Debug.WriteLine($"{DateTime.Now} Starting SQL execution {executionId} on context:{this._ID}");
                        _CurrentCommand!.CommandText = queriesByType.ToString();
                        if (_UseDependency)
                        {
                            _CurrentCommand.Notification = null;
                            SqlDependency dependency = new SqlDependency(_CurrentCommand);
                            dependency.OnChange += new OnChangeEventHandler(OnChange);
                        }
                        _CurrentCommand.Parameters.Clear();
                        _CurrentCommand.Parameters.TryAddRange(queriesByType.AllQueryParameters.GetSQLParams());
                        await _CurrentCommand.ExecuteNonQueryAsync(token.Value);
                        Debug.WriteLine($"{DateTime.Now} Finished SQL execution {executionId} in {timer.ElapsedMilliseconds} ms");
                        timer.Stop();
                        timer = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Unable to execute SQL on DB. {ex.Message}", ex);
            }
        }


        public DataTable GetData(string query) => GetData(query, parameters: null);
        public DataTable GetData(string query, IQueryParameter? parameter)=> GetData(query, parameter?.CreateCollection());
        public DataTable GetData(string query, IQueryParameterCollection? parameters)
        {
            var newQuery = "";
            if (parameters != null && parameters.Count() > 0)
            {
                string? append = null;
                if (query.Contains("ORDER BY"))
                {
                    int ii = query.LastIndexOf("ORDER BY");
                    append = query.Substring(ii);
                    query = query.Remove(ii, query.Length - ii);
                }
                var sb = new StringBuilder($"{query} WHERE ");
                for (int i = 0; i < parameters.Count(); i++)
                    sb.Append($"{(i > 0 ? " AND " : "")}{SqlServerQueryRenderer.WhereCondition((Parameter)parameters[i])}");
                sb.Append($" {append ?? ""}");
                newQuery = sb.ToString();
            }
            else
            {
                newQuery = query;
            }
            return RunQuery(newQuery, parameters);
        }

        public DataTable GetData(IQuery query) =>
            RunQuery(query.BuildQuery(), ((Query)query).Parameters.AsQueryParameters());


        //public Task<DataTable> GetDataAsync(string query) => 
        //    GetDataAsync(query, null, CancellationToken.None);
        //public Task<DataTable> GetDataAsync(string query, CancellationToken token) => 
        //    GetDataAsync(query, null, token);
        //public Task<DataTable> GetDataAsync(string query, IQueryParameter? parameter) => 
        //    GetDataAsync(query, parameter?.CreateCollection(), CancellationToken.None);
        //public Task<DataTable> GetDataAsync(string query, IQueryParameter? parameter, CancellationToken token) =>
        //    GetDataAsync(query, parameter?.CreateCollection(), token);


        //public Task<DataTable> GetDataAsync(string query, IQueryParameterCollection? parameters, CancellationToken token)
        //{
        //    var newQuery = "";
        //    if (parameters != null && parameters.Count > 0)
        //    {
        //        string? append = null;
        //        if (query.Contains("ORDER BY"))
        //        {
        //            int ii = query.LastIndexOf("ORDER BY");
        //            append = query.Substring(ii);
        //            query = query.Remove(ii, query.Length - ii);
        //        }
        //        var sb = new StringBuilder($"{query} WHERE ");
        //        for (int i = 0; i < parameters.Count; i++)
        //            sb.Append($"{(i > 0 ? " AND " : "")}{parameters[i].SQLWhereCondition}");
        //        sb.Append($" {append ?? ""}");
        //        newQuery = sb.ToString();
        //    }
        //    else
        //    {
        //        newQuery = query;
        //    }
        //    return RunQueryAsync(newQuery, parameters);
        //}

        //public Task<DataTable> GetDataAsync(IQuery? query) => _GetDataAsync(query);

        //private Task<DataTable> _GetDataAsync(IQuery? query)
        //{
        //    ArgumentNullException.ThrowIfNull(query);
        //    throw new NotImplementedException();
        //    //return RunQueryAsync(query.ToString(), query.Where.AllParameters());
        //}
        ////private Task<DataTable> _GetDataAsync(NestedQuery nestedQuery)
        ////{
        ////    throw new NotImplementedException();
        ////}

        public DataSet GetDataSet(IQuery query)
        {
            try
            {
                if(query == null || query is not Query)
                    throw new SqlDataContextException("Invalid Query type", SqlDataContextExceptionType.InvalidIQueryType);
                var sqlQuery = query as Query;
                if (_CurrentConnection == null)
                    throw new SqlDataContextException("No active Connection", SqlDataContextExceptionType.CurrentConnectionIsNull);
                _CurrentCommand = _CurrentConnection.CreateCommand();
                if (HasTransaction && _CurrentTransaction != null)
                    _CurrentCommand.Transaction = _CurrentTransaction;
                _CurrentCommand.CommandText = sqlQuery!.BuildQuery();
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(sqlQuery.GetSQLParams());
                var dataSet = DataSet.Initialize(sqlQuery.TableSchema);
                //using (SqlDataAdapter SQLDA = new SqlDataAdapter(_CurrentCommand))
                //{
                //    var DT = new DataTable();
                //    SQLDA.Fill(DT);
                //}

                using var reader = _CurrentCommand.ExecuteReader(CommandBehavior.CloseConnection);
                if (reader != null && reader.HasRows)
                {
                    while (reader.Read())
                        reader.GetValues(dataSet.GetNewRow());
                }
                if (reader != null && sqlQuery is SelectQuery { WithTotalCount: true } &&
                    reader.NextResult() && reader.Read())
                    dataSet.TotalRowCount = reader.GetInt64(0);
                if (reader != null && sqlQuery is SelectQuery)
                    ReadChildResults(reader, sqlQuery, dataSet);
                return dataSet;
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }


        public Task<DataSet> GetDataSetAsync(Query query) => GetDataSetAsync(query, CancellationToken.None);
        public async Task<DataSet> GetDataSetAsync(Query query, CancellationToken token)
        {
            try
            {
                if (_CurrentConnection == null)
                    throw new SqlDataContextException("No active Connection", SqlDataContextExceptionType.CurrentConnectionIsNull);
                _CurrentCommand = _CurrentConnection.CreateCommand();
                if (HasTransaction && _CurrentTransaction != null)
                    _CurrentCommand.Transaction = _CurrentTransaction;
                _CurrentCommand.CommandText = query.BuildQuery();
                if (_UseDependency)
                {
                    _CurrentCommand.Notification = null;
                    SqlDependency dependency = new(_CurrentCommand);
                    dependency.OnChange += new OnChangeEventHandler(OnChange);
                }
                _CurrentCommand.Parameters.TryAddRange(query.GetSQLParams());
                var dataSet = DataSet.Initialize(query.TableSchema);
                using var reader = await _CurrentCommand.ExecuteReaderAsync(CommandBehavior.CloseConnection, token);
                if (reader != null && reader.HasRows)
                {
                    while (await reader.ReadAsync(token))
                        reader.GetValues(dataSet.GetNewRow());
                }
                if (reader != null && query is SelectQuery { WithTotalCount: true } &&
                    await reader.NextResultAsync(token) && await reader.ReadAsync(token))
                    dataSet.TotalRowCount = reader.GetInt64(0);
                if (reader != null && query is SelectQuery)
                    await ReadChildResultsAsync(reader, query, dataSet, token);
                return dataSet;
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error returning Query from DB. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Reads the result set of each child collection query, in the pre-order the batch
        /// renders them, into the data set's linked data sets.
        /// </summary>
        private static void ReadChildResults(SqlDataReader reader, Query query, DataSet dataSet)
        {
            foreach (var childQuery in query.LinkedQueries)
            {
                if (!reader.NextResult())
                    throw new SqlDataContextException($"The batch returned no result set for the child query on {childQuery.TableName}.", SqlDataContextExceptionType.MissingResultSet);
                var childSet = DataSet.Initialize(childQuery.TableSchema);
                while (reader.Read())
                    reader.GetValues(childSet.GetNewRow());
                dataSet.LinkedDataSets.Add(childSet);
                ReadChildResults(reader, childQuery, childSet);
            }
        }

        /// <inheritdoc cref="ReadChildResults"/>
        private static async Task ReadChildResultsAsync(SqlDataReader reader, Query query, DataSet dataSet, CancellationToken token)
        {
            foreach (var childQuery in query.LinkedQueries)
            {
                if (!await reader.NextResultAsync(token))
                    throw new SqlDataContextException($"The batch returned no result set for the child query on {childQuery.TableName}.", SqlDataContextExceptionType.MissingResultSet);
                var childSet = DataSet.Initialize(childQuery.TableSchema);
                while (await reader.ReadAsync(token))
                    reader.GetValues(childSet.GetNewRow());
                dataSet.LinkedDataSets.Add(childSet);
                await ReadChildResultsAsync(reader, childQuery, childSet, token);
            }
        }

        /// <summary>
        /// Streams the query's own rows. A select's child collection queries are left out of
        /// the command: the caller fetches them itself, and sending them here only transferred
        /// every child row to be drained unread.
        /// </summary>
        public async IAsyncEnumerable<StreamingRow> StreamAsync(IQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (query == null || query is not Query sqlQuery)
                throw new SqlDataContextException("Invalid Query type", SqlDataContextExceptionType.InvalidIQueryType);
            if (_CurrentConnection == null)
                throw new SqlDataContextException("No active Connection", SqlDataContextExceptionType.CurrentConnectionIsNull);

            _CurrentCommand = _CurrentConnection.CreateCommand();
            if (HasTransaction && _CurrentTransaction != null)
                _CurrentCommand.Transaction = _CurrentTransaction;
            _CurrentCommand.CommandText = sqlQuery is SelectQuery select
                ? select.BuildQuery(includeChildQueries: false)
                : sqlQuery.BuildQuery();
            if (_UseDependency)
            {
                _CurrentCommand.Notification = null;
                SqlDependency dependency = new(_CurrentCommand);
                dependency.OnChange += new OnChangeEventHandler(OnChange);
            }
            _CurrentCommand.Parameters.TryAddRange(sqlQuery.GetSQLParams());

            var row = new StreamingRow([.. sqlQuery.TableSchema!.Columns.Select(x => x.ColumnName)]);

            SqlDataReader? reader = null;
            try
            {
                reader = await _CurrentCommand.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception($"Error streaming Query from DB. {ex.Message}", ex);
            }

            await using (reader)
            {
                if (reader != null && reader.HasRows)
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        reader.GetValues(row.Values);
                        yield return row;
                    }
                }
            }
        }

        public void BulkUpdate(string TableName, DataTable DT)
        {
            try
            {
                using (SqlBulkCopy SBC = new SqlBulkCopy(_CurrentConnection, SqlBulkCopyOptions.Default, _CurrentTransaction))
                {
                    SBC.DestinationTableName = TableName;
                    SBC.WriteToServer(DT);
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception("Error performing SQL Bulk Update for data. " + ex.Message, ex);
            }
        }
        public async Task BulkUpdateAsync(string tableName, DataTable dataTable)
        {
            try
            {
                using (SqlBulkCopy SBC = new SqlBulkCopy(_CurrentConnection, SqlBulkCopyOptions.Default, _CurrentTransaction))
                {
                    SBC.DestinationTableName = tableName;
                    await SBC.WriteToServerAsync(dataTable);
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception("Error performing SQL Bulk Update for data. " + ex.Message, ex);
            }
        }
        public void BulkInsert(string tableName, DataTable dataTable)
        {
            try
            {
                using (SqlBulkCopy SBC = new SqlBulkCopy(_CurrentConnection, SqlBulkCopyOptions.Default, _CurrentTransaction))
                {
                    SBC.DestinationTableName = tableName;
                    
                    foreach (var col in dataTable.Columns)
                        SBC.ColumnMappings.Add(col.ToString(), col.ToString());
                    SBC.WriteToServer(dataTable);
                }
            }
            catch (Exception ex)
            {
                if(HasTransaction)
                    Rollback();
                throw new Exception("Error performing SQL Bulk Insert for data. " + ex.Message, ex);
            }
        }
        public async Task BulkInsertAsync(string tableName, DataTable DT)
        {
            try
            {
                using (var SBC = new SqlBulkCopy(_CurrentConnection, SqlBulkCopyOptions.Default, _CurrentTransaction))
                {
                    SBC.DestinationTableName = tableName;
                    foreach (var col in DT.Columns)
                        SBC.ColumnMappings.Add(col.ToString(), col.ToString());
                    await SBC.WriteToServerAsync(DT);
                }
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception("Error performing SQL Bulk Insert for data. " + ex.Message, ex);
            }
        }


        public void SetData(string query) => _SetData(query, null);
        public void SetData(string query, IQueryParameter? parameter) => _SetData(query, parameter?.CreateCollection());
        public void SetData(string query, IQueryParameterCollection? parameters) => _SetData(query, parameters);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "<Pending>")]
        private void _SetData(string query, IQueryParameterCollection? parameters)
        {
            try
            {
                _CurrentCommand!.CommandText = query;
                _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
                _CurrentCommand.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if(HasTransaction)
                    Rollback();
                throw new Exception("Unable to write data to DB. " + ex.Message, ex);
            }
        }

        public Task SetDataAsync(string query) => _SetDataAsync(query, null);
        public Task SetDataAsync(string query, IQueryParameter? parameter) => _SetDataAsync(query, parameter?.CreateCollection());
        public Task SetDataAsync(string query, IQueryParameterCollection? parameters) => _SetDataAsync(query, parameters);
        private async Task _SetDataAsync(string query, IQueryParameterCollection? parameters)
        {
            try
            {
                _CurrentCommand!.CommandText = query;
                _CurrentCommand.Parameters.TryAddRange(parameters?.GetSQLParams());
                await _CurrentCommand.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                if (HasTransaction)
                    Rollback();
                throw new Exception("Unable to write data to DB. " + ex.Message, ex);
            }
        }

        
        //public void UpdateData(IQueryParameterCollection updateParams, string tablename, Guid id)
        //{
        //    UpdateData(updateParams, tablename, (IQueryParameterCollection)new Parameter("ID", id));
        //}
        //public void UpdateData(ParameterCollection updateParams, string tablename, string whereClause)
        //{
        //    SetData(updateParams.CreateUpdateStatement(tablename, whereClause), updateParams);
        //}
        //public void UpdateData(ParameterCollection updateParams, string tablename, ParameterCollection whereParams)
        //{
        //    SetData(updateParams.CreateUpdateStatement(tablename,whereParams),updateParams.CopyAndCombine(whereParams));
        //}

        //public Task UpdateDataAsync(ParameterCollection updateParams, string tablename, Guid id) => 
        //    UpdateDataAsync(updateParams, tablename, (ParameterCollection)new Parameter("ID", id));
        //public Task UpdateDataAsync(ParameterCollection updateParams, string tablename, string whereClause)=>
        //    SetDataAsync(updateParams.CreateUpdateStatement(tablename, whereClause), updateParams);
        //public Task UpdateDataAsync(ParameterCollection updateParams, string tablename, ParameterCollection whereParams) =>
        //    SetDataAsync(updateParams.CreateUpdateStatement(tablename, whereParams), updateParams.CopyAndCombine(whereParams));


        //public void InsertData(ParameterCollection parameters, string tablename)=>
        //    SetData(parameters.CreateInsertStatement(tablename), parameters);

        //public Task InsertDataAsync(ParameterCollection parameters, string tablename) =>
        //    SetDataAsync(parameters.CreateInsertStatement(tablename), parameters);
        //public Guid InsertDataReturnGuid(ParameterList parameters, string tablename)
        //{
        //    string Query = $"DECLARE @NewID UNIQUEIDENTIFIER; SET @NewID = NEWID(); INSERT INTO {tablename} (ID,";
        //    if(parameters!=null)
        //        foreach (var p in parameters)
        //            Query += $" {p.LeftOperand},";
        //    Query = $"{Query.Remove(Query.Length - 1)}) VALUES (@NewID,";

        //    if (parameters != null)
        //        foreach (var p in parameters)
        //            Query += $" {p.RightOperand},";

        //    Query = $"{Query.Remove(Query.Length - 1)});SELECT @NewID;";
        //    DataTable DT = RunQuery(Query, parameters);
        //    if (DT.Rows.Count < 1)
        //        return Guid.Empty;
        //    else
        //        return Guid.Parse(DT.Rows[0][0].ToString());

        //}



        //[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "<Pending>")]
        //public static bool TableExists(string TableName)
        //{
        //    using (var db = new Context())
        //    {
        //        using (SqlConnection connection = db.BuildNewSQLConnection())
        //        {
        //            string strCheckTable =
        //               String.Format(
        //                  "IF OBJECT_ID('{0}', 'U') IS NOT NULL SELECT 'true' ELSE SELECT 'false'",
        //                  TableName);
        //            connection.Open();
        //            using (var command = new SqlCommand(strCheckTable, connection))
        //            {
        //                command.CommandType = CommandType.Text;
        //                return Convert.ToBoolean(command.ExecuteScalar());
        //            }
        //        }
        //    }
        //}

        public void OnChange(object sender, SqlNotificationEventArgs e)
        {
            SqlDependency? dependency = sender as SqlDependency;
            // Notices are only a one shot deal
            // so remove the existing one so a new 
            // one can be added
            if (dependency == null)
                return;
            dependency.OnChange -= OnChange;
            // Fire the event
            if (OnNewMessage != null)
            {
                OnNewMessage();
            }
        }



        


        //public enum SqlFunctions
        //{
        //    SYSDATETIME,
        //    SYSDATETIMEOFFSET,
        //    ISNULL,
        //    ISNOTNULL,
        //}

        public override string ToString()
        {
            return $"Database object {_ID} - using Connection {_CurrentConnection?.ClientConnectionId.ToString() ?? "error: null connection"}";
        }
    }
    public class SqlDataContextException : Exception
    {
        public SqlDataContextExceptionType Type { get; private set; }
        
        internal SqlDataContextException()
        { 
            Type = SqlDataContextExceptionType.Unspecified;
        }
        internal SqlDataContextException(SqlDataContextExceptionType type) : base()
        {
            Type = type;
        }
        internal SqlDataContextException(string message, SqlDataContextExceptionType type) : base(message)
        {
            Type = type;
        }

    }
    public enum SqlDataContextExceptionType
    {
        UnableToCreateNewCommand = 0,
        CurrentCommandIsNull = 10,
        CurrentConnectionIsNull = 20,
        InvalidIQueryType = 30,
        /// <summary>A batch returned fewer result sets than the queries it was built from.</summary>
        MissingResultSet = 40,
        Unspecified = 99,
    }


}
