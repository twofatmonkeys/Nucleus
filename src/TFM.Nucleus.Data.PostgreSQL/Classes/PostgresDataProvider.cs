using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// The PostgreSQL provider: the shared relational provider with PostgreSQL's renderer,
    /// schema dialect, contexts and LISTEN/NOTIFY change subscriptions.
    /// </summary>
    internal sealed class PostgresDataProvider(
        IObjectSchemaProvider objectSchemaProvider,
        IOptionsMonitor<NucleusOptions> nucleusOptions,
        IOptionsMonitor<PostgresConfiguration> config,
        ILogger<PostgresDataProvider>? logger, ILogger<SchemaManager>? schemaLogger)
        : RelationalDataProvider(objectSchemaProvider, nucleusOptions, logger, schemaLogger)
    {
        private PostgresQueryRenderer? _renderer;
        private PostgresSchemaStore? _schemaStore;

        internal PostgresConfiguration Config => config.CurrentValue;

        /// <summary>The schema the tables live in, fixed when the provider first renders a query.</summary>
        internal string SchemaName => ((PostgresQueryRenderer)Renderer).Schema;

        public override RelationalProviderOptions Options => Config;

        public override IQueryRenderer Renderer => _renderer ??= new PostgresQueryRenderer(Config.Schema);

        protected override string EngineName => "PostgreSQL";

        protected override ISchemaStore SchemaStore => _schemaStore ??= new PostgresSchemaStore(this, SchemaLogger);

        public override DataProviderCapabilities Capabilities => new()
        {
            ParameterPrefix = "@",
            MaxParametersPerCommand = Config.MaxParametersPerQuery,
            SupportsBulkOperations = true,
            SupportsChangeNotifications = true,
            SupportsOffsetPaging = true,
        };

        internal PostgresDataContext CreatePostgresDataContext(PostgresDataContext? parent, bool useTransaction, bool useDependency, bool bypassChecks = false)
        {
            if (!bypassChecks)
                EnsureReady();
            return PostgresDataContext.Create(this, parent, useTransaction, useDependency);
        }

        internal async Task<PostgresDataContext> CreatePostgresDataContextAsync(PostgresDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token, bool bypassChecks = false)
        {
            if (!bypassChecks)
                await EnsureReadyAsync();
            return await PostgresDataContext.CreateAsync(this, parent, useTransaction, useDependency, token);
        }

        protected override IDataContext CreateDataContext(IDataContext? parent, bool useTransaction, bool useDependency) =>
            CreatePostgresDataContext(parent as PostgresDataContext, useTransaction, useDependency);

        protected override async Task<IDataContext> CreateDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token) =>
            await CreatePostgresDataContextAsync(parent as PostgresDataContext, useTransaction, useDependency, token);

        /// <summary>
        /// Subscribes to changes of <typeparamref name="T"/>'s table: every statement that writes
        /// it, from any session, notifies the table's channel through its trigger.
        /// </summary>
        public override Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) =>
            PostgresChangeSubscription<T>.CreateAsync(this, token);

        protected override Exception ProviderException(DataProviderErrorKind kind, string? message = null) =>
            message == null ? new PostgresDataProviderException(kind) : new PostgresDataProviderException(message, kind);

        protected override bool TryGetUniqueViolation(Exception exception, out string? physicalIndexName) =>
            PostgresErrors.TryGetUniqueViolation(exception, out physicalIndexName);
    }

    /// <summary>What the PostgreSQL provider gives its <see cref="SchemaManager"/>.</summary>
    internal sealed class PostgresSchemaStore : ISchemaStore
    {
        private readonly PostgresDataProvider _provider;
        private readonly PostgresCatalogReader _catalog;
        private readonly PostgresSchemaDialect _dialect;

        public PostgresSchemaStore(PostgresDataProvider provider, ILogger? logger)
        {
            _provider = provider;
            _catalog = new PostgresCatalogReader(() => provider.Config);
            _dialect = new PostgresSchemaDialect(provider.SchemaName, new PostgresSchemaExtras(_catalog, () => _dialect!, logger));
        }

        public Type? RootType => _provider.RootType;

        public bool ReadOnlySchema => _provider.NucleusOptions.ReadOnlySchema;

        public TableNamingPolicy TableNaming => _provider.Options.TableNaming;

        public bool RenameFullNameTables => _provider.Options.RenameFullNameTables;

        public ISchemaDialect Dialect => _dialect;

        public ICatalogReader Catalog => _catalog;

        /// <summary>A rebuild runs on one transactional context; PostgreSQL's DDL is transactional, so it commits or rolls back whole.</summary>
        public async ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken) =>
            new DbSchemaTransaction<PostgresDataContext>(
                async
                    ? await _provider.CreatePostgresDataContextAsync(null, true, false, cancellationToken, true)
                    : _provider.CreatePostgresDataContext(null, true, false, true),
                table => new SchemaStatement(PostgresCatalogReader.ColumnsQuery(_provider.SchemaName, table)),
                PostgresCatalogReader.ReadColumn);
    }

    /// <summary>The PostgreSQL provider could not be configured, initialised or reached.</summary>
    public class PostgresDataProviderException : DataProviderException
    {
        internal PostgresDataProviderException(DataProviderErrorKind kind) : base(kind)
        {
        }

        internal PostgresDataProviderException(string message, DataProviderErrorKind kind) : base(message, kind)
        {
        }
    }
}
