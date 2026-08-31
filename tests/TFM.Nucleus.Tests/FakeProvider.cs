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

        public Task<IDataContext> CreateContextAsync(ContextOptions? options = null, CancellationToken token = default) =>
            Task.FromResult(CreateContext(options));

        /// <summary>Capabilities say unsupported, so the seam contract is to throw.</summary>
        public Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException("FakeDataProvider does not support change notifications.");

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
        public bool RolledBack { get; private set; }

        public bool HasTransaction => options.UseTransaction;
        public bool HasDependency => options.UseDependency;
        public bool HasChildren => false;
        public IDataContext? Parent => options.Parent;

        public TFM.Nucleus.DataSet GetDataSet(IQuery query) =>
            provider.BuildDataSet(((FakeQuery)query).EntityType);
        public Task<TFM.Nucleus.DataSet> GetDataSetAsync(IQuery query) => Task.FromResult(GetDataSet(query));
        public Task<TFM.Nucleus.DataSet> GetDataSetAsync(IQuery query, CancellationToken token) => Task.FromResult(GetDataSet(query));

        public void ExecuteQuery(IQuery query) => ExecutedQueries.Add(query);
        public Task ExecuteQueryAsync(IQuery query) { ExecutedQueries.Add(query); return Task.CompletedTask; }
        public Task ExecuteQueryAsync(IQuery query, CancellationToken token) => ExecuteQueryAsync(query);
        public void ExecuteQueries(IQuery[] queries) => ExecutedQueries.AddRange(queries);
        public Task ExecuteQueriesAsync(IQuery[] queries) { ExecutedQueries.AddRange(queries); return Task.CompletedTask; }
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
                var row = new StreamingRow(dataSet.ColumnNames);
                dataSet[i].CopyTo(row.Values, 0);
                yield return row;
            }
        }

        public void Save(ICoreObject obj) { }
        public void Delete(ICoreObject obj) { }
        public Task SaveAsync(ICoreObject obj) => Task.CompletedTask;
        public Task DeleteAsync(ICoreObject obj) => Task.CompletedTask;
        public void BulkSave(IEnumerable<ICoreObject?>? objects) { }
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects) => Task.CompletedTask;
        public Task BulkSaveAsync(IEnumerable<ICoreObject?>? objects, CancellationToken token) => Task.CompletedTask;

        public void Rollback() => RolledBack = true;
        public void TryRollback() => RolledBack = true;
        public Task TryRollbackAsync() { RolledBack = true; return Task.CompletedTask; }
        public Task TryRollbackAsync(CancellationToken token) { RolledBack = true; return Task.CompletedTask; }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
