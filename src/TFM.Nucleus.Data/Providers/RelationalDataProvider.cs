using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The <see cref="IDataProvider"/> every relational provider shares: status and
    /// initialisation through a <see cref="SchemaManager"/>, the query factories (each query
    /// carries the provider's <see cref="Renderer"/>), table lookup, context creation and the
    /// translation of a database failure into a neutral exception. A provider supplies its
    /// options, renderer, schema store, contexts, capabilities and change subscriptions.
    /// </summary>
    public abstract class RelationalDataProvider : IDataProvider
    {
        private readonly IOptionsMonitor<NucleusOptions> _nucleusOptions;
        private readonly ILogger? _logger;
        private readonly ILogger<SchemaManager>? _schemaLogger;
        private IObjectSchemaProvider _objectSchemaProvider;
        private SchemaManager? _schema;

        protected RelationalDataProvider(IObjectSchemaProvider objectSchemaProvider, IOptionsMonitor<NucleusOptions> nucleusOptions,
            ILogger? logger, ILogger<SchemaManager>? schemaLogger)
        {
            _objectSchemaProvider = objectSchemaProvider;
            _nucleusOptions = nucleusOptions;
            _logger = logger;
            _schemaLogger = schemaLogger;
        }

        /// <summary>The provider's current options.</summary>
        public abstract RelationalProviderOptions Options { get; }

        public NucleusOptions NucleusOptions => _nucleusOptions.CurrentValue;

        /// <summary>Writes this provider's queries in its dialect.</summary>
        public abstract IQueryRenderer Renderer { get; }

        /// <summary>The prefix of a bound parameter's name in command text.</summary>
        public virtual string ParameterPrefix => "@";

        /// <summary>The engine, for log and error messages.</summary>
        protected abstract string EngineName { get; }

        /// <summary>The dialect and database access the schema manager uses.</summary>
        protected abstract ISchemaStore SchemaStore { get; }

        public abstract DataProviderCapabilities Capabilities { get; }

        protected abstract IDataContext CreateDataContext(IDataContext? parent, bool useTransaction, bool useDependency);

        protected abstract Task<IDataContext> CreateDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token);

        public abstract Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new();

        /// <summary>
        /// True when the exception, or one in its inner chain, is the engine's unique violation;
        /// gives the name of the broken index when the engine reports one.
        /// </summary>
        protected abstract bool TryGetUniqueViolation(Exception exception, out string? physicalIndexName);

        /// <summary>The provider's exception for a failure of the given kind.</summary>
        protected abstract Exception ProviderException(DataProviderErrorKind kind, string? message = null);

        /// <summary>The logger the schema manager and the provider's schema extras write to.</summary>
        protected ILogger<SchemaManager>? SchemaLogger => _schemaLogger;

        /// <summary>The tables this provider stores and their state in the database.</summary>
        public SchemaManager Schema => _schema ??= new SchemaManager(SchemaStore, _objectSchemaProvider, _schemaLogger);

        public DataProviderStatus Status => MapSchemaStatus(Schema.Status);

        public Guid ConnectionIdentifier => Options.ID;

        public Type? RootType { get; set; }

        #region Initialisation

        public DataProviderStatus Initialize(IObjectSchemaProvider? objectSchema = null) =>
            InitializeCore(objectSchema, async: false, CancellationToken.None).GetAwaiter().GetResult();

        public Task<DataProviderStatus> InitializeAsync(IObjectSchemaProvider? objectSchema = null, CancellationToken? cancellationToken = null) =>
            InitializeCore(objectSchema, async: true, cancellationToken ?? CancellationToken.None).AsTask();

        private async ValueTask<DataProviderStatus> InitializeCore(IObjectSchemaProvider? objectSchema, bool async, CancellationToken cancellationToken)
        {
            if (Status == DataProviderStatus.Ok)
                return Status;
            try
            {
                if (objectSchema != null)
                {
                    _logger?.LogInformation("Initializing the {engine} data provider with the provided object schema.", EngineName);
                    _objectSchemaProvider = objectSchema;
                    _schema = null;
                }
                var schemaStatus = async ? await Schema.InitializeAsync(cancellationToken) : Schema.Initialize();
                if (schemaStatus != EDataSchemaStatus.Ok)
                {
                    _logger?.LogError("Error initializing the {engine} data schema.", EngineName);
                    throw schemaStatus switch
                    {
                        EDataSchemaStatus.DatasourceNotFound => ProviderException(DataProviderErrorKind.DatabaseNonExistent, "Database not found during schema initialization."),
                        EDataSchemaStatus.NotCurrent => ProviderException(DataProviderErrorKind.SchemaNotCurrent, "Database schema is not current."),
                        EDataSchemaStatus.NotInitialized => ProviderException(DataProviderErrorKind.NotInitialized, "Database schema is not initialized."),
                        EDataSchemaStatus.Error => ProviderException(DataProviderErrorKind.Error, $"Error initializing the {EngineName} data schema."),
                        _ => ProviderException(DataProviderErrorKind.Unspecified, $"Unspecified error initializing the {EngineName} data schema."),
                    };
                }

                return DataProviderStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing the {engine} data provider", EngineName);
                return DataProviderStatus.Error;
            }
        }

        /// <summary>Initialises on first use and refuses to hand out a context while the provider is not ready.</summary>
        protected void EnsureReady()
        {
            if (Status == DataProviderStatus.Uninitialized)
                Initialize();
            if (Status != DataProviderStatus.Ok)
                throw ProviderException(DataProviderErrorKind.NotInitialized);
        }

        /// <inheritdoc cref="EnsureReady"/>
        protected async Task EnsureReadyAsync()
        {
            if (Status == DataProviderStatus.Uninitialized)
                await InitializeAsync();
            if (Status != DataProviderStatus.Ok)
                throw ProviderException(DataProviderErrorKind.NotInitialized);
        }

        #endregion

        #region Queries and tables

        public SelectQuery ParseSelectExpression<T>(Expression expression)
        {
            var tblSchema = Schema.GetTableSchema<T>() ?? throw new Exception($"Error retrieving the table schema for type: {typeof(T).FullName}, not found.");
            var query = new SelectQuery(Renderer, tblSchema, Schema.GetTableSchema);
            return query.Parse(expression);
        }

        public UpdateQuery? CreateUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            UpdateQuery.Create(Renderer, GetTableSchema<T>(), objs);

        public InsertQuery? CreateInsertQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            InsertQuery.Create(Renderer, GetTableSchema<T>(), objs);

        /// <summary>
        /// Deletes the given objects by ID, with every row of their child collections, whether
        /// or not those were loaded. Null when there is nothing to delete.
        /// </summary>
        public DeleteWhereQuery? CreateDeleteQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null)
                return null;
            return CreateDeleteByIdQuery(GetTableSchema<T>(), objs.Where(o => o != null && !o.IsNew).Select(o => o.ID));
        }

        /// <summary>Deletes rows of one table by ID, cascading to child collection rows.</summary>
        public DeleteWhereQuery? CreateDeleteByIdQuery(TableSchema tableSchema, IEnumerable<Guid> ids) =>
            DeleteWhereQuery.ForIds(Renderer, tableSchema, Schema.GetTableSchema, ids);

        /// <summary>
        /// Deletes the rows of <typeparamref name="T"/> matching a predicate body, cascading to
        /// child collection rows. The caller checks the predicate with
        /// <see cref="ExpressionParser.CheckDeletePredicate"/> first.
        /// </summary>
        public DeleteWhereQuery CreateDeleteWhereQuery<T>(Expression predicate) where T : ICoreObject<T>, new()
        {
            var query = new DeleteWhereQuery(Renderer, GetTableSchema<T>(), Schema.GetTableSchema);
            return (DeleteWhereQuery)ExpressionParser.ParseExpressionToQuery(query, predicate);
        }

        public Query? CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null || !objs.Any()) return null;
            var schema = GetTableSchema<T>();
            var update = UpdateQuery.Create(Renderer, schema, objs.Where(x => !x.IsNew));
            var insert = InsertQuery.Create(Renderer, schema, objs.Where(x => x.IsNew));
            if (insert != null) { if (update != null) insert.LinkedQueries.Add(update); return insert; }
            return update;
        }

        public TableSchema GetTableSchema(Type type) =>
            Schema.GetTableSchema(type) ?? throw new Exception($"Error retrieving the table schema for type: {type.FullName}, not found.");

        public TableSchema GetTableSchema<T>() => GetTableSchema(typeof(T));

        #endregion

        #region Failures

        /// <summary>
        /// Wraps a database failure with a message: a unique violation becomes a
        /// <see cref="UniqueViolationException"/> naming the entity and column of a Nucleus index,
        /// anything else a plain exception. The original is the inner exception either way.
        /// </summary>
        public Exception TranslateFailure(string message, Exception exception)
        {
            if (!TryGetUniqueViolation(exception, out var physicalIndexName))
                return new Exception(message, exception);
            var (entityType, columnName, indexName) = ResolveUniqueIndex(physicalIndexName);
            return new UniqueViolationException(message, indexName ?? physicalIndexName, entityType, columnName, exception);
        }

        private (Type? EntityType, string? ColumnName, string? IndexName) ResolveUniqueIndex(string? physicalIndexName)
        {
            if (physicalIndexName == null)
                return default;
            var dialect = SchemaStore.Dialect;
            foreach (var table in Schema.TableCache.Values)
                foreach (var column in table.IndexedColumns.Where(c => c.Unique))
                    if (dialect.IdentifierComparer.Equals(SchemaComparer.PhysicalIndexName(table, column, dialect), physicalIndexName))
                        return (table.ObjectSchema.Type, column.ColumnName, IndexNaming.ColumnIndexName(table, column));
            return default;
        }

        #endregion

        #region IDataProvider
        ISelectQuery IDataProvider.ParseSelectExpression<T>(Expression expression) => ParseSelectExpression<T>(expression);
        IUpdateQuery? IDataProvider.CreateUpdateQuery<T>(IEnumerable<T>? objs) => CreateUpdateQuery(objs);
        IInsertQuery? IDataProvider.CreateInsertQuery<T>(IEnumerable<T>? objs) => CreateInsertQuery(objs);
        IQuery? IDataProvider.CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) => CreateInsertAndUpdateQuery(objs);
        IDeleteQuery? IDataProvider.CreateDeleteQuery<T>(IEnumerable<T>? objs) => CreateDeleteQuery(objs);

        IDataContext IDataProvider.CreateContext(ContextOptions? options)
        {
            options ??= ContextOptions.Default;
            return CreateDataContext(options.Parent, options.UseTransaction, options.UseDependency);
        }

        Task<IDataContext> IDataProvider.CreateContextAsync(ContextOptions? options, CancellationToken token)
        {
            options ??= ContextOptions.Default;
            return CreateDataContextAsync(options.Parent, options.UseTransaction, options.UseDependency, token);
        }

        async Task<DataProviderStatus> IDataProvider.CheckDataContextAsync()
        {
            try
            {
                return await SchemaStore.Catalog.DatabaseExistsAsync(true, CancellationToken.None)
                    ? DataProviderStatus.Ok
                    : DataProviderStatus.NotAvailable;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error probing the {engine} connection", EngineName);
                return DataProviderStatus.NotAvailable;
            }
        }

        DataProviderStatus IDataProvider.Initialize(IEnumerable<IObjectSchema>? objectSchemas) => Initialize();
        Task<DataProviderStatus> IDataProvider.InitializeAsync(IObjectSchemaProvider? objectSchema) => InitializeAsync(objectSchema);

        DataProviderStatus IDataProvider.CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(Schema.CheckSchema());
        async Task<DataProviderStatus> IDataProvider.CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(await Schema.CheckSchemaAsync());

        void IDataProvider.UpdateSchema(IEnumerable<IObjectSchema> objectSchemas) => Schema.Update();
        Task IDataProvider.UpdateSchemaAsync(IObjectSchema objectSchema) => Schema.UpdateAsync();
        Task IDataProvider.UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas) => Schema.UpdateAsync();

        private static DataProviderStatus MapSchemaStatus(EDataSchemaStatus status) => status switch
        {
            EDataSchemaStatus.Ok => DataProviderStatus.Ok,
            EDataSchemaStatus.NotCurrent => DataProviderStatus.IncompatibleSchema,
            EDataSchemaStatus.NotInitialized => DataProviderStatus.Uninitialized,
            EDataSchemaStatus.DatasourceNotFound => DataProviderStatus.NotAvailable,
            EDataSchemaStatus.Error => DataProviderStatus.Error,
            _ => DataProviderStatus.Unknown,
        };
        #endregion
    }

    /// <summary>A provider could not be configured, initialised or reached.</summary>
    public class DataProviderException : Exception
    {
        public DataProviderErrorKind Kind { get; }

        public DataProviderException(DataProviderErrorKind kind) : base()
        {
            Kind = kind;
        }

        public DataProviderException(string? message, DataProviderErrorKind kind) : base(message)
        {
            Kind = kind;
        }
    }

    public enum DataProviderErrorKind
    {
        NotInitialized = 0,
        NotConfigured = 1,
        SchemaNotCurrent = 10,
        DatabaseUnavailable = 20,
        DatabaseNonExistent = 21,
        Error = 90,
        Unspecified = 99,
    }
}
