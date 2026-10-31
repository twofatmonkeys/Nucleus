using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Boots the full stack (AddNucleus plus one provider) against that provider's test database
    /// and initializes the schema once for the collection. When the provider's database is not
    /// configured the fixture stays unconfigured and each test skips instead of failing, so
    /// `dotnet test` passes on machines without a test database (CI included). Every neutral
    /// suite is an abstract class run once per provider by a sealed subclass in that provider's
    /// collection.
    /// </summary>
    public abstract class DatabaseFixture : IAsyncLifetime
    {
        public TestConfig.DatabaseSettings? Settings { get; }
        public IDataProvider? Provider { get; private set; }
        public ObjectDataManager? Manager { get; private set; }
        private ServiceProvider? _services;

        protected DatabaseFixture(TestConfig.DatabaseSettings? settings)
        {
            Settings = settings;
        }

        /// <summary>The provider, in skip messages and lock names.</summary>
        public abstract string ProviderName { get; }

        /// <summary>How to configure this provider's test database, for the skip message.</summary>
        protected abstract string ConfigurationHint { get; }

        /// <summary>Registers this provider against the test database.</summary>
        protected abstract void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings);

        public async ValueTask InitializeAsync()
        {
            if (Settings == null)
                return;

            var services = new ServiceCollection();
            var log = new CapturingLoggerProvider();
            services.AddLogging(builder => builder.AddProvider(log));
            services.AddNucleus();
            AddProvider(services, Settings);
            _services = services.BuildServiceProvider();

            Provider = _services.GetRequiredService<IDataProvider>();
            var status = await Provider.InitializeAsync();
            if (status != DataProviderStatus.Ok)
                throw new InvalidOperationException($"{ProviderName} test schema initialization failed: {status}{Environment.NewLine}{string.Join(Environment.NewLine, log.Entries)}");
            Manager = new ObjectDataManager(Provider, _services.GetRequiredService<IObjectSchemaProvider>(), null);
        }

        public ValueTask DisposeAsync()
        {
            _services?.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        /// <summary>Returns the settings, or dynamically skips the calling test when unconfigured.</summary>
        public TestConfig.DatabaseSettings RequireSettings()
        {
            Assert.SkipWhen(Settings == null, $"{ProviderName} test database not configured - {ConfigurationHint}");
            return Settings!;
        }

        /// <summary>Returns the initialized manager, or dynamically skips when unconfigured.</summary>
        public ObjectDataManager RequireManager()
        {
            RequireSettings();
            return Manager!;
        }

        /// <summary>
        /// The cross-process lock of a suite that resets its tables, per provider: the net8.0 and
        /// net10.0 runs share a database, but two providers never do.
        /// </summary>
        internal Task<TestDatabaseLock> LockAsync(string suite) => TestDatabaseLock.AcquireAsync($"{suite}-{ProviderName}");
    }

    /// <summary>SQL Server, configured by the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.</summary>
    public sealed class SqlServerFixture() : DatabaseFixture(TestConfig.TryGetSettings())
    {
        public override string ProviderName => "sqlserver";

        protected override string ConfigurationHint => "set the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.";

        protected override void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings) =>
            services.AddNucleusSQLDataProvider(options =>
            {
                options.Server = settings.Server;
                options.Database = settings.Database;
                options.Username = settings.Username;
                options.Password = settings.Password;
            });
    }

    [CollectionDefinition(Name)]
    public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
    {
        public const string Name = "SqlServer";
    }

    /// <summary>PostgreSQL, configured by the Nucleus:Postgres:* user secrets or NUCLEUS_PG_TEST_* environment variables.</summary>
    public sealed class PostgresFixture() : DatabaseFixture(TestConfig.TryGetPostgresSettings())
    {
        public override string ProviderName => "postgres";

        protected override string ConfigurationHint => "set the Nucleus:Postgres:* user secrets or NUCLEUS_PG_TEST_* environment variables.";

        protected override void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings) =>
            services.AddNucleusPostgresDataProvider(options =>
            {
                options.Server = settings.Server;
                options.Database = settings.Database;
                options.Username = settings.Username;
                options.Password = settings.Password;
            });
    }

    [CollectionDefinition(Name)]
    public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
    {
        public const string Name = "Postgres";
    }
}
