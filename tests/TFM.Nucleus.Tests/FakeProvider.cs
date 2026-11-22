using System.Linq.Expressions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A complete, SQL-free IDataProvider. It proves the provider seam: the neutral layers
    /// (ObjectDataManager, ObjectDataAdaptor, ExpressionParser) must be able to run entirely
    /// against this fake — FakeQuery.BuildQuery() throws, so any neutral code that asks for
    /// query text fails structurally.
    /// </summary>
    public sealed class FakeDataProvider : IDataProvider
    {
        private readonly Dictionary<Type, List<Dictionary<string, object?>>> _rows = [];
        public List<FakeDataContext> Contexts { get; } = [];
        public List<object> CreatedQueries { get; } = [];

        /// <summary>The row count every delete by predicate on this provider's contexts reports.</summary>
        public int RowsAffectedByDeleteWhere { get; set; }

        /// <summary>When set, every query execution on this provider's contexts throws it, as a database failure would.</summary>
        public Exception? ExecuteFailure { get; set; }

        /// <summary>
        /// When set, committing a context throws it, as a transactional context does when its
        /// commit fails: the statements ran, the commit did not.
        /// </summary>
        public Exception? CommitFailure { get; set; }

        /// <summary>
        /// Failures thrown once each, in order, by the next executions, reads, deletes by predicate
        /// and stream starts on this provider's contexts, as a database that fails and then recovers.
        /// </summary>
        public Queue<Exception> PendingFailures { get; } = [];

        /// <summary>When set, a stream on this provider's contexts throws it after handing out this many rows, once.</summary>
        public (int AfterRows, Exception Failure)? StreamFailure { get; set; }

        /// <summary>Throws the next pending failure, if there is one.</summary>
        internal void FailIfPending()
        {
            if (PendingFailures.TryDequeue(out var failure))
                throw failure;
        }

        /// <inheritdoc />
        public int RetryAttempts { get; set; } = 1;

        public Guid ConnectionIdentifier { get; } = Guid.NewGuid();
        public DataProviderStatus Status => DataProviderStatus.Ok;
        public Type? RootType { get; set; }

        public DataProviderCapabilities Capabilities { get; } = new()
        {
            ParameterPrefix = "@",
            MaxParametersPerCommand = 2000,
        };

        public void SetupRows<T>(params Dictionary<string, object?>[] rows) =>
            _rows[typeof(T)] = [.. rows];

        internal TFM.Nucleus.DataSet BuildDataSet(Type entityType)
        {
            var schema = new FakeTableSchema(ObjectSchema.Get(entityType));
            var dataSet = TFM.Nucleus.DataSet.Initialize(schema);
            if (_rows.TryGetValue(entityType, out var rows))
                foreach (var row in rows)
                    dataSet.FillNext([.. dataSet.ColumnNames.Select(c => row.GetValueOrDefault(c))]);
            return dataSet;
        }

        public DataProviderStatus Initialize(IEnumerable<IObjectSchema>? objectSchemas = null) => DataProviderStatus.Ok;
        public Task<DataProviderStatus> InitializeAsync(IObjectSchemaProvider? objectSchema = null) => Task.FromResult(DataProviderStatus.Ok);

        public ISelectQuery ParseSelectExpression<T>(Expression expression)
        {
            var query = new FakeQuery(typeof(T), FakeQueryKind.Select);
            CreatedQueries.Add(query);
            return (ISelectQuery)ExpressionParser.ParseExpressionToQuery(query, expression);
        }

        public IUpdateQuery? CreateUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            Track(FakeQuery.ForObjects(typeof(T), FakeQueryKind.Update, objs));
        public IInsertQuery? CreateInsertQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            Track(FakeQuery.ForObjects(typeof(T), FakeQueryKind.Insert, objs));
        public IQuery? CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            Track(FakeQuery.ForObjects(typeof(T), FakeQueryKind.InsertAndUpdate, objs));
        public IDeleteQuery? CreateDeleteQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            Track(FakeQuery.ForObjects(typeof(T), FakeQueryKind.Delete, objs));

        private FakeQuery? Track(FakeQuery? query)
        {
            if (query != null)
                CreatedQueries.Add(query);
            return query;
        }

        public IDataContext CreateContext(ContextOptions? options = null)
        {
            var context = new FakeDataContext(this, options ?? ContextOptions.Default);
            Contexts.Add(context);
            return context;
        }

        /// <summary>The token each async context was created with, in order.</summary>
        public List<CancellationToken> ContextTokens { get; } = [];

        public Task<IDataContext> CreateContextAsync(ContextOptions? options = null, CancellationToken token = default)
        {
            ContextTokens.Add(token);
            return Task.FromResult(CreateContext(options));
        }

        /// <summary>The entity type of every subscription asked of this provider, in order.</summary>
        public List<Type> SubscriptionRequests { get; } = [];

        /// <summary>Capabilities say unsupported, so the seam contract is to throw, after recording the request.</summary>
        public Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new()
        {
            SubscriptionRequests.Add(typeof(T));
            throw new NotSupportedException("FakeDataProvider does not support change notifications.");
        }

        public Task<DataProviderStatus> CheckDataContextAsync() => Task.FromResult(DataProviderStatus.Ok);
        public DataProviderStatus CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas) => DataProviderStatus.Ok;
        public Task<DataProviderStatus> CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas) => Task.FromResult(DataProviderStatus.Ok);
        public void UpdateSchema(IEnumerable<IObjectSchema> objectSchemas) { }
        public Task UpdateSchemaAsync(IObjectSchema objectSchema) => Task.CompletedTask;
        public Task UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas) => Task.CompletedTask;
    }

    public enum FakeQueryKind { Select, Insert, Update, InsertAndUpdate, Delete }

    public sealed class FakeQuery(Type entityType, FakeQueryKind kind)
        : IQuery, ISelectQuery, IInsertQuery, IUpdateQuery, IDeleteQuery
    {
        public Type EntityType { get; } = entityType;
        public FakeQueryKind Kind { get; } = kind;
        public List<object?> Objects { get; } = [];
        public List<IQueryParameter> WhereParameters { get; } = [];
        public List<(string Property, bool Descending)> Orderings { get; } = [];
        private readonly List<IQuery> _linkedQueries = [];

        public static FakeQuery? ForObjects<T>(Type entityType, FakeQueryKind kind, IEnumerable<T>? objs)
        {
            if (objs == null || !objs.Any())
                return null;
            var query = new FakeQuery(entityType, kind);
            query.Objects.AddRange(objs.Cast<object?>());
            return query;
        }

        /// <summary>Neutral code must never ask a query for its text — that is provider-internal.</summary>
        public string BuildQuery() =>
            throw new InvalidOperationException("Neutral code requested query text from the fake provider — the provider seam is broken.");

        public IDataTableSchema? DataTableSchema => null;
        public IEnumerable<IQueryParameter?>? SetParameters => [];
        public IEnumerable<IQueryParameter?>? AllQueryParameters => WhereParameters;
        public IEnumerable<IQueryParameter?>? AllQueryOutputParameters => [];
        public IEnumerable<IQuery> Split(int maxParameters) => [this];
        public bool TryAddWhere(IQueryParameter parameter) { WhereParameters.Add(parameter); return true; }
        public void AddWhere(IQueryParameter parameter) => WhereParameters.Add(parameter);
        public void AddWhere(IWhereCollection whereCollection) { }
        public void AddWhereOr(IQueryParameter parameter) => WhereParameters.Add(parameter);
        public void AddWhereOr(IWhereCollection whereCollection) { }
        public void AddChildQuery(IQuery childQuery, string linkedColumn) => _linkedQueries.Add(childQuery);
        public void AddLinkedQuery(IQuery? query) { if (query != null) _linkedQueries.Add(query); }
        public IEnumerable<IQuery> FlattenLinkedQueries() => [this, .. _linkedQueries];
        public IEnumerable<IQuery> LinkedQueries => _linkedQueries;
        public int CountLinkedQueries() => _linkedQueries.Count;
        public IWhereCollection NewWhereCollection() => new FakeWhereCollection(this);

        // ISelectQuery
        ISelectQuery ISelectQuery.Create(string tablename, Type objectType) => new FakeQuery(objectType, FakeQueryKind.Select);
        ISelectQuery ISelectQuery.Create(string tablename, string[]? columnConstraints, Type objectType) => new FakeQuery(objectType, FakeQueryKind.Select);
        public int Skip { get; set; }
        public int Take { get; set; }
        public bool WithTotalCount { get; set; }
        public bool IncludeDeleted { get; set; }
        public void AddOrderBy(string propertyName, bool descending = false) => Orderings.Add((propertyName, descending));

        /// <summary>Every child collection a load excluded from this query.</summary>
        public List<string> ExcludedCollections { get; } = [];
        public bool ExcludeChildCollection(string propertyName)
        {
            ExcludedCollections.Add(propertyName);
            return true;
        }

        /// <summary>Every lazy child collection a load included in this query.</summary>
        public List<string> IncludedCollections { get; } = [];
        public bool IncludeChildCollection(string propertyName)
        {
            IncludedCollections.Add(propertyName);
            return true;
        }

        // IInsertQuery / IUpdateQuery
        IInsertQuery IInsertQuery.Create(string tablename) => new FakeQuery(EntityType, FakeQueryKind.Insert);
        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, Guid idValue) => new FakeQuery(EntityType, FakeQueryKind.Update);
        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, int idValue) => new FakeQuery(EntityType, FakeQueryKind.Update);
    }

    /// <summary>Collects parameters straight onto the owning query; grouping is not modelled.</summary>
    public sealed class FakeWhereCollection(FakeQuery owner) : IWhereCollection
    {
        public void AddAnd(IQueryParameter parameter) => owner.WhereParameters.Add(parameter);
        public void AddOr(IQueryParameter parameter) => owner.WhereParameters.Add(parameter);
        public void AddWhereAnd(IWhereCollection whereCollection) { }
        public void AddWhereOr(IWhereCollection whereCollection) { }
    }

    public sealed class FakeDataContext(FakeDataProvider provider, ContextOptions options) : IDataContext
    {
        public List<IQuery> ExecutedQueries { get; } = [];

        /// <summary>The token each async execution was given, in order.</summary>
        public List<CancellationToken> ExecutionTokens { get; } = [];
        public bool RolledBack { get; private set; }

        public bool HasTransaction => options.UseTransaction;
        public bool WatchesForChanges => options.WatchForChanges;
        public bool HasDependency => options.WatchForChanges;
        public bool HasChildren => false;
        public IDataContext? Parent => options.Parent;

        public TFM.Nucleus.DataSet GetDataSet(IQuery query)
        {
            provider.FailIfPending();
            return provider.BuildDataSet(((FakeQuery)query).EntityType);
        }
        public Task<TFM.Nucleus.DataSet> GetDataSetAsync(IQuery query) => Task.FromResult(GetDataSet(query));
        public Task<TFM.Nucleus.DataSet> GetDataSetAsync(IQuery query, CancellationToken token) => Task.FromResult(GetDataSet(query));

        public void ExecuteQuery(IQuery query)
        {
            if (provider.ExecuteFailure is { } failure)
                throw failure;
            provider.FailIfPending();
            ExecutedQueries.Add(query);
        }
        public Task ExecuteQueryAsync(IQuery query)
        {
            ExecuteQuery(query);
            return Task.CompletedTask;
        }
        public Task ExecuteQueryAsync(IQuery query, CancellationToken token)
        {
            ExecutionTokens.Add(token);
            return ExecuteQueryAsync(query);
        }
        public void ExecuteQueries(IQuery[] queries)
        {
            provider.FailIfPending();
            ExecutedQueries.AddRange(queries);
        }
        public Task ExecuteQueriesAsync(IQuery[] queries) { ExecuteQueries(queries); return Task.CompletedTask; }
        public Task ExecuteQueriesAsync(IQuery[] queries, CancellationToken token) => ExecuteQueriesAsync(queries);

        public IEnumerable<T> Load<T>(Expression expression) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException("Use ObjectDataManager.Load in seam tests.");
        public Task<IEnumerable<T>> LoadAsync<T>(Expression expression) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException("Use ObjectDataManager.Load in seam tests.");

        public async IAsyncEnumerable<StreamingRow> StreamAsync(IQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var dataSet = GetDataSet(query);
            for (int i = 0; i < dataSet.RowCount; i++)
            {
                await Task.Yield();
                if (provider.StreamFailure is { } failure && failure.AfterRows == i)
                {
                    provider.StreamFailure = null;
                    throw failure.Failure;
                }
                var row = new StreamingRow(dataSet.ColumnNames);
                dataSet[i].CopyTo(row.Values, 0);
                yield return row;
            }
        }

        public void Save(ICoreObject obj) { }
        public void Delete(ICoreObject obj) { }
        public Task SaveAsync(ICoreObject obj) => Task.CompletedTask;
        public Task SaveAsync(ICoreObject obj, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(ICoreObject obj) => Task.CompletedTask;
        public Task DeleteAsync(ICoreObject obj, CancellationToken token) => Task.CompletedTask;

        /// <summary>Every predicate a delete by predicate received, with its entity type.</summary>
        public List<(Type EntityType, LambdaExpression Predicate)> DeletedWhere { get; } = [];

        public int DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new()
        {
            provider.FailIfPending();
            DeletedWhere.Add((typeof(T), predicate));
            return provider.RowsAffectedByDeleteWhere;
        }

        public Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            Task.FromResult(DeleteWhere(predicate));
        public void BulkSave(IEnumerable<ICoreObject?>? objects) => provider.FailIfPending();
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects)
        {
            BulkSave(objects);
            return Task.CompletedTask;
        }
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token) => BulkSaveAsync(objects);

        /// <summary>How the context was committed: null if it was not, else true for CommitAsync.</summary>
        public bool? CommittedAsync { get; private set; }

        public void Commit()
        {
            if (provider.CommitFailure is { } failure)
                throw failure;
            CommittedAsync = false;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (provider.CommitFailure is { } failure)
                throw failure;
            CommittedAsync = true;
            return Task.CompletedTask;
        }

        public void Rollback() => RolledBack = true;
        public void TryRollback() => RolledBack = true;
        public Task TryRollbackAsync() { RolledBack = true; return Task.CompletedTask; }
        public Task TryRollbackAsync(CancellationToken token) { RolledBack = true; return Task.CompletedTask; }

        /// <summary>How the context was disposed: null while open, else true for DisposeAsync.</summary>
        public bool? DisposedAsync { get; private set; }

        public void Dispose() => DisposedAsync ??= false;

        public ValueTask DisposeAsync()
        {
            DisposedAsync ??= true;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class FakeTableSchema(IObjectSchema objectSchema) : IDataTableSchema
    {
        private readonly IDataColumnSchema[] _columns =
            [.. objectSchema.AllDBColumns.Select(p => (IDataColumnSchema)new FakeColumnSchema(p))];

        public string TableName => objectSchema.Name;
        public IEnumerable<IDataColumnSchema> Columns => _columns;
        public IDataColumnSchema? IdentityColumn => FindByPropertyName("ID");
        public IObjectSchema ObjectSchema => objectSchema;
        public IDataColumnSchema? FindByPropertyName(string propertyName) =>
            _columns.FirstOrDefault(c => c.PropertySchema.Name == propertyName);
        public IDataColumnSchema? FindByColumnName(string columnName) =>
            _columns.FirstOrDefault(c => c.ColumnName == columnName);
        public IDataColumnSchema this[string columnName] =>
            FindByColumnName(columnName) ?? throw new KeyNotFoundException(columnName);
        public bool TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema)
        {
            dataTableSchema = FindByPropertyName(propertyName);
            return dataTableSchema != null;
        }
        public bool TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema)
        {
            dataTableSchema = FindByColumnName(columnName);
            return dataTableSchema != null;
        }
    }

    public sealed class FakeColumnSchema(IPropertySchema property) : IDataColumnSchema
    {
        public string ColumnName => property.Name;
        public bool IsNullable => true;
        public Type DataType => property.Type;
        public IPropertySchema PropertySchema => property;
        public object? GetClrValue(object? value) => value;
    }
}
