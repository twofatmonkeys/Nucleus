using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The SQL Server provider: the shared relational provider with SQL Server's renderer,
    /// schema dialect, contexts and query-notification subscriptions.
    /// </summary>
    internal partial class SQLDataProvider(
        IObjectSchemaProvider objectSchemaProvider,
        IOptionsMonitor<NucleusOptions> nucleusOptions,
        IOptionsMonitor<SQLConfiguration> sqlConfig,
        ILogger<SQLDataProvider>? logger, ILogger<SchemaManager>? schemaLogger)
        : RelationalDataProvider(objectSchemaProvider, nucleusOptions, logger, schemaLogger)
    {
        private SqlServerSchemaStore? _schemaStore;

        internal SQLConfiguration SqlConfig => sqlConfig.CurrentValue;

        public override RelationalProviderOptions Options => SqlConfig;

        public override IQueryRenderer Renderer => SqlServerQueryRenderer.Instance;

        protected override string EngineName => "SQL Server";

        protected override ISchemaStore SchemaStore => _schemaStore ??= new SqlServerSchemaStore(this, SchemaLogger);

        public override DataProviderCapabilities Capabilities => new()
        {
            ParameterPrefix = "@",
            MaxParametersPerCommand = SqlConfig.MaxParametersPerQuery,
            SupportsBulkOperations = true,
            SupportsChangeNotifications = true,
            SupportsOffsetPaging = true,
        };

        internal SqlDataContext CreateSqlDataContext(SqlDataContext? parent, bool useTransaction, bool useDependency, bool bypassChecks = false)
        {
            if (!bypassChecks)
                EnsureReady();
            return SqlDataContext.Create(this, parent, useTransaction, useDependency);
        }

        internal async Task<SqlDataContext> CreateSqlDataContextAsync(SqlDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token, bool bypassChecks = false)
        {
            if (!bypassChecks)
                await EnsureReadyAsync();
            return await SqlDataContext.CreateAsync(this, parent, useTransaction, useDependency, token);
        }

        protected override IDataContext CreateDataContext(IDataContext? parent, bool useTransaction, bool useDependency) =>
            CreateSqlDataContext(parent as SqlDataContext, useTransaction, useDependency);

        protected override async Task<IDataContext> CreateDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token) =>
            await CreateSqlDataContextAsync(parent as SqlDataContext, useTransaction, useDependency, token);

        /// <summary>
        /// Subscribes to SQL Server query-notification driven change events for
        /// <typeparamref name="T"/>. The subscription fires <see cref="IChangeSubscription.Changed"/>
        /// whenever the entity's table changes outside this process; dispose it to stop watching.
        /// </summary>
        public override Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) =>
            SqlQuerySubscription<T>.CreateAsync(this, token);

        protected override Exception ProviderException(DataProviderErrorKind kind, string? message = null) =>
            message == null
                ? new SqlDataProviderException((SqlDataProviderExceptionType)kind)
                : new SqlDataProviderException(message, (SqlDataProviderExceptionType)kind);

        // 2601: duplicate key in a unique index; 2627: duplicate key in a unique constraint.
        // Both messages quote the index or constraint name.
        [GeneratedRegex(@"(?:unique index|constraint) '(?<name>[^']+)'", RegexOptions.IgnoreCase)]
        private static partial Regex IndexNameRegex();

        protected override bool TryGetUniqueViolation(Exception exception, out string? physicalIndexName)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is SqlException { Number: 2601 or 2627 } sql)
                {
                    var match = IndexNameRegex().Match(sql.Message);
                    physicalIndexName = match.Success ? match.Groups["name"].Value : null;
                    return true;
                }
            }
            physicalIndexName = null;
            return false;
        }
    }

    public class SqlDataProviderException : DataProviderException
    {
        public SqlDataProviderExceptionType Type => (SqlDataProviderExceptionType)Kind;

        internal SqlDataProviderException() : base(DataProviderErrorKind.Unspecified)
        {
        }
        internal SqlDataProviderException(SqlDataProviderExceptionType type) : base((DataProviderErrorKind)type)
        {
        }
        internal SqlDataProviderException(string message, SqlDataProviderExceptionType type) : base(message, (DataProviderErrorKind)type)
        {
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
