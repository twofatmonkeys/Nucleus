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
            MaxParametersPerCommand = SqlConfig.MaxParametersPerCommand,
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
                ? new SqlDataProviderException(kind)
                : new SqlDataProviderException(message, kind);

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

        // 547 for a key: "The INSERT statement conflicted with the FOREIGN KEY constraint "FK_..."",
        // or "... the REFERENCE constraint ..." for a delete; the name is quoted.
        [GeneratedRegex(@"constraint ""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
        private static partial Regex KeyNameRegex();

        protected override bool TryGetForeignKeyViolation(Exception exception, out string? physicalKeyName)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is SqlException sql && SqlServerErrors.Classify(sql).Kind == DatabaseErrorKind.ForeignKeyViolation)
                {
                    var match = KeyNameRegex().Match(sql.Message);
                    physicalKeyName = match.Success ? match.Groups["name"].Value : null;
                    return true;
                }
            }
            physicalKeyName = null;
            return false;
        }

        protected override DatabaseErrorClass ClassifyFailure(Exception exception) => SqlServerErrors.Classify(exception);

        // 334: SQL Server refuses an OUTPUT clause without INTO on a table with an enabled
        // trigger. Nucleus writes one for every column marked ReturnOutputToProperty.
        protected override Exception? TranslateEngineFailure(string message, Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
                if (e is SqlException { Number: 334 } sql)
                    return new SqlDataProviderException(
                        $"{message} The write reads a [DatabaseColumn(ReturnOutputToProperty = true)] value back with an OUTPUT " +
                        $"clause, which SQL Server refuses on a table with an enabled trigger. Disable the trigger, or remove " +
                        $"ReturnOutputToProperty from the entity's columns. {sql.Message}",
                        DataProviderErrorKind.Error, exception);
            return null;
        }
    }

    public class SqlDataProviderException : DataProviderException
    {
        /// <summary>The same value as <see cref="DataProviderException.Kind"/>, under this provider's own enum.</summary>
        [Obsolete("Use Kind, which every provider's exception has.")]
#pragma warning disable CS0618 // the obsolete enum is this obsolete member's type
        public SqlDataProviderExceptionType Type => (SqlDataProviderExceptionType)Kind;
#pragma warning restore CS0618

        internal SqlDataProviderException() : base(DataProviderErrorKind.Unspecified)
        {
        }
        internal SqlDataProviderException(DataProviderErrorKind kind) : base(kind)
        {
        }
        internal SqlDataProviderException(string message, DataProviderErrorKind kind) : base(message, kind)
        {
        }
        internal SqlDataProviderException(string message, DataProviderErrorKind kind, Exception innerException) : base(message, kind, innerException)
        {
        }

    }
    /// <summary>A copy of <see cref="DataProviderErrorKind"/>, value for value.</summary>
    [Obsolete("Use DataProviderErrorKind, which every provider reports through DataProviderException.Kind.")]
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
