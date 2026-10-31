using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    public class SQLDataProvider(
        IObjectSchemaProvider objectSchemaProvider, 
        IOptionsMonitor<NucleusOptions> nucleusOptions, 
        IOptionsMonitor<SQLConfiguration> sqlConfig, 
        ILogger<SQLDataProvider>? logger, ILogger<SqlDataSchema>? schemaLogger) : IDataProvider
    {
        private readonly ILogger? _logger = logger;
        private IObjectSchemaProvider _objectSchemaProvider = objectSchemaProvider;
        private readonly IOptionsMonitor<NucleusOptions> _nucleusOptions = nucleusOptions;
        private readonly IOptionsMonitor<SQLConfiguration> _sqlConfig = sqlConfig;
        private SqlDataSchema? _sqlDataSchema;
        //private DataProviderStatus? _status;

        protected internal SQLConfiguration SqlConfig => _sqlConfig.CurrentValue;
        protected internal NucleusOptions NucleusOptions => _nucleusOptions.CurrentValue;

        private SqlDataSchema SqlDataSchema { get => _sqlDataSchema ??= CreateSqlDataSchema(); }

        private SqlDataSchema CreateSqlDataSchema() => new(this, _objectSchemaProvider, schemaLogger);

        public DataProviderStatus Status => SqlDataSchema.Status switch
        {
            EDataSchemaStatus.Ok => DataProviderStatus.Ok,
            EDataSchemaStatus.NotInitialized => DataProviderStatus.Uninitialized,
            EDataSchemaStatus.NotCurrent => DataProviderStatus.IncompatibleSchema,
            EDataSchemaStatus.DatasourceNotFound => DataProviderStatus.NotAvailable,
            EDataSchemaStatus.Error => DataProviderStatus.Error,
            _ => DataProviderStatus.Unknown,
        };

        
        public Guid ConnectionIdentifier => _sqlConfig.CurrentValue.ID;

        //public async Task<DataProviderStatus> CheckDataContextAsync()
        //{
        //    await Task.Yield(); // Simulate async operation
        //    if (Status == DataProviderStatus.Ok)
        //        return Status;
        //    else
        //    {
        //        //todo initilization logic

        //        return DataProviderStatus.NotConfigured;
        //    }
        //}

        public DataProviderStatus Initialize(IObjectSchemaProvider? objectSchema = null)
        {
            if(Status == DataProviderStatus.Ok)
                return Status;
            try
            {
                if (objectSchema != null)
                {
                    _logger?.LogInformation("Initializing SQL Data Provider with provided object schema.");
                    _objectSchemaProvider = objectSchema;
                    _sqlDataSchema = null;
                }
                var schemaStatus = SqlDataSchema.Initialize();
                if (schemaStatus != EDataSchemaStatus.Ok)
                {
                    _logger?.LogError("Error initializing SQL Data Schema.");
                    if (schemaStatus == EDataSchemaStatus.DatasourceNotFound)
                        throw new SqlDataProviderException("Database not found during schema initialization.", SqlDataProviderExceptionType.DatabaseNonExistent);
                    else if (schemaStatus == EDataSchemaStatus.NotCurrent)
                        throw new SqlDataProviderException("Database schema is not current.", SqlDataProviderExceptionType.SchemaNotCurrent);
                    else if (schemaStatus == EDataSchemaStatus.NotInitialized)
                        throw new SqlDataProviderException("Database schema is not initialized.", SqlDataProviderExceptionType.NotInitialized);
                    else if (schemaStatus == EDataSchemaStatus.Error)
                        throw new SqlDataProviderException("Error initializing SQL Data Schema.", SqlDataProviderExceptionType.Error);
                    else
                        throw new SqlDataProviderException("Unspecified error initializing SQL Data Schema.", SqlDataProviderExceptionType.Unspecified);
                }

                return DataProviderStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing SQL Data Provider");
                return DataProviderStatus.Error;
            }
        }
        public async Task<DataProviderStatus> InitializeAsync(IObjectSchemaProvider? objectSchema = null, CancellationToken? cancellationToken = null)
        {
            if (Status == DataProviderStatus.Ok)
                return Status;
            try
            {
                if (objectSchema != null)
                {
                    _logger?.LogInformation("Initializing SQL Data Provider with provided object schema.");
                    _objectSchemaProvider = objectSchema;
                    _sqlDataSchema = null;
                }
                var schemaStatus = await SqlDataSchema.InitializeAsync(cancellationToken);
                if(schemaStatus != EDataSchemaStatus.Ok)
                {
                    _logger?.LogError("Error initializing SQL Data Schema.");
                    if(schemaStatus == EDataSchemaStatus.DatasourceNotFound)
                        throw new SqlDataProviderException("Database not found during schema initialization.", SqlDataProviderExceptionType.DatabaseNonExistent);
                    else if(schemaStatus == EDataSchemaStatus.NotCurrent)
                        throw new SqlDataProviderException("Database schema is not current.", SqlDataProviderExceptionType.SchemaNotCurrent);
                    else if(schemaStatus == EDataSchemaStatus.NotInitialized)
                        throw new SqlDataProviderException("Database schema is not initialized.", SqlDataProviderExceptionType.NotInitialized);
                    else if(schemaStatus == EDataSchemaStatus.Error)
                        throw new SqlDataProviderException("Error initializing SQL Data Schema.", SqlDataProviderExceptionType.Error);
                    else
                        throw new SqlDataProviderException("Unspecified error initializing SQL Data Schema.", SqlDataProviderExceptionType.Unspecified);
                }

                return DataProviderStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing SQL Data Provider");
                return DataProviderStatus.Error;
            }

        }

        private IDataContext CreateIDataContext(IDataContext? parent, bool useTransaction, bool useDependency)
        {
            return CreateSqlDataContext(parent as SqlDataContext, useTransaction, useDependency);
        }
        protected internal SqlDataContext CreateSqlDataContext(SqlDataContext? parent, bool useTransaction, bool useDependency, bool bypassChecks = false)
        {
            if (!bypassChecks)
            {
                if (Status == DataProviderStatus.Uninitialized)
                    Initialize();
                if (Status != DataProviderStatus.Ok)
                    throw new SqlDataProviderException(SqlDataProviderExceptionType.NotInitialized);
            }
            return SqlDataContext.CreateContext(parent, useTransaction, useDependency, _sqlConfig, _nucleusOptions, this);
        }

        private async Task<IDataContext> CreateIDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token)
        {
            return await CreateSqlDataContextAsync(parent as SqlDataContext, useTransaction, useDependency, token);
        }
        protected internal async Task<SqlDataContext> CreateSqlDataContextAsync(SqlDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token, bool bypassChecks = false)
        {
            if (!bypassChecks)
            {
                if (Status == DataProviderStatus.Uninitialized)
                    await InitializeAsync();
                if (Status != DataProviderStatus.Ok)
                    throw new SqlDataProviderException(SqlDataProviderExceptionType.NotInitialized);
            }
            return await SqlDataContext.CreateContextAsync(parent, useTransaction, useDependency, token, _sqlConfig, _nucleusOptions, this);
        }

        public SelectQuery ParseSelectExpression<T>(Expression expression)
        {
            var tblSchema = SqlDataSchema.GetTableSchema<T>() ?? throw new Exception($"Error retreiving Sql Table schema for type: {typeof(T).FullName}, not found.");
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tblSchema, SqlDataSchema.GetTableSchema);
            return query.Parse(expression);
        }

        public UpdateQuery? CreateUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            return UpdateQuery.Create(SqlServerQueryRenderer.Instance, GetTableSchema<T>(), objs);
        }

        public InsertQuery? CreateInsertQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            return InsertQuery.Create(SqlServerQueryRenderer.Instance, GetTableSchema<T>(), objs);
        }

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
        public DeleteWhereQuery? CreateDeleteByIdQuery(SqlTableSchema tableSchema, IEnumerable<Guid> ids) =>
            DeleteWhereQuery.ForIds(SqlServerQueryRenderer.Instance, tableSchema, SqlDataSchema.GetTableSchema, ids);

        /// <summary>
        /// Deletes the rows of <typeparamref name="T"/> matching a predicate body, cascading to
        /// child collection rows. The caller checks the predicate with
        /// <see cref="ExpressionParser.CheckDeletePredicate"/> first.
        /// </summary>
        public DeleteWhereQuery CreateDeleteWhereQuery<T>(Expression predicate) where T : ICoreObject<T>, new()
        {
            var query = new DeleteWhereQuery(SqlServerQueryRenderer.Instance, GetTableSchema<T>(), SqlDataSchema.GetTableSchema);
            return (DeleteWhereQuery)ExpressionParser.ParseExpressionToQuery(query, predicate);
        }

        public Query? CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null || !objs.Any()) return null;
            var schema = GetTableSchema<T>();
            var update = UpdateQuery.Create(SqlServerQueryRenderer.Instance, schema, objs.Where(x => !x.IsNew));
            var insert = InsertQuery.Create(SqlServerQueryRenderer.Instance, schema, objs.Where(x => x.IsNew));
            if (insert != null) { if (update != null) insert.LinkedQueries.Add(update); return insert; }
            return update;
        }

        /// <summary>
        /// Subscribes to SQL Server query-notification driven change events for
        /// <typeparamref name="T"/>. The subscription fires <see cref="IChangeSubscription.Changed"/>
        /// whenever the entity's table changes outside this process; dispose it to stop watching.
        /// </summary>
        public Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new() =>
            SqlQuerySubscription<T>.CreateAsync(this, token);

        public SqlTableSchema GetTableSchema(Type type)
        {
            return SqlDataSchema.GetTableSchema(type) ?? throw new Exception($"Error retreiving Sql Table schema for type: {type.FullName}, not found.");
        }

        public SqlTableSchema GetTableSchema<T>()
        {
            return SqlDataSchema.GetTableSchema<T>() ?? throw new Exception($"Error retreiving Sql Table schema for type: {typeof(T).FullName}, not found.");
        }

        #region IDataProvider
        //DataProviderStatus IDataProvider.Status => Status;
        Guid IDataProvider.ConnectionIdentifier => ConnectionIdentifier;

        ISelectQuery IDataProvider.ParseSelectExpression<T>(Expression expression) => ParseSelectExpression<T>(expression);
        IUpdateQuery? IDataProvider.CreateUpdateQuery<T>(IEnumerable<T>? objs) => CreateUpdateQuery(objs);
        IInsertQuery? IDataProvider.CreateInsertQuery<T>(IEnumerable<T>? objs) => CreateInsertQuery(objs);
        IQuery? IDataProvider.CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) => CreateInsertAndUpdateQuery(objs);
        IDeleteQuery? IDataProvider.CreateDeleteQuery<T>(IEnumerable<T>? objs) => CreateDeleteQuery(objs);

        public Type? RootType { get; set; }

        public DataProviderCapabilities Capabilities => new()
        {
            ParameterPrefix = "@",
            MaxParametersPerCommand = SqlConfig.MaxParametersPerQuery,
            SupportsBulkOperations = true,
            SupportsChangeNotifications = true,
            SupportsOffsetPaging = true,
        };

        IDataContext IDataProvider.CreateContext(ContextOptions? options)
        {
            options ??= ContextOptions.Default;
            return CreateIDataContext(options.Parent, options.UseTransaction, options.UseDependency);
        }

        Task<IDataContext> IDataProvider.CreateContextAsync(ContextOptions? options, CancellationToken token)
        {
            options ??= ContextOptions.Default;
            return CreateIDataContextAsync(options.Parent, options.UseTransaction, options.UseDependency, token);
        }

        async Task<DataProviderStatus> IDataProvider.CheckDataContextAsync()
        {
            try
            {
                return await SqlConfig.DatabaseExistsAsync()
                    ? DataProviderStatus.Ok
                    : DataProviderStatus.NotAvailable;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error probing SQL Server connection");
                return DataProviderStatus.NotAvailable;
            }
        }

        DataProviderStatus IDataProvider.Initialize(IEnumerable<IObjectSchema>? objectSchemas) => Initialize();
        Task<DataProviderStatus> IDataProvider.InitializeAsync(IObjectSchemaProvider? objectSchema) => InitializeAsync(objectSchema);

        DataProviderStatus IDataProvider.CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(SqlDataSchema.CheckSchema());
        async Task<DataProviderStatus> IDataProvider.CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas) =>
            MapSchemaStatus(await SqlDataSchema.CheckSchemaAsync());

        void IDataProvider.UpdateSchema(IEnumerable<IObjectSchema> objectSchemas) => SqlDataSchema.Update();
        Task IDataProvider.UpdateSchemaAsync(IObjectSchema objectSchema) => SqlDataSchema.UpdateAsync();
        Task IDataProvider.UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas) => SqlDataSchema.UpdateAsync();

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

    public class SqlDataProviderException : Exception
    {
        public SqlDataProviderExceptionType Type { get; private set; }

        internal SqlDataProviderException()
        {
            Type = SqlDataProviderExceptionType.Unspecified;
        }
        internal SqlDataProviderException(SqlDataProviderExceptionType type) : base()
        {
            Type = type;
        }
        internal SqlDataProviderException(string message, SqlDataProviderExceptionType type) : base(message)
        {
            Type = type;
        }

    }
    public enum SqlDataProviderExceptionType
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

