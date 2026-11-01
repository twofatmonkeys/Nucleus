using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;

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
        public IDataProvider? Provider => _stack?.Provider;
        public ObjectDataManager? Manager => _stack?.Manager;
        private TestStack? _stack;

        protected DatabaseFixture(TestConfig.DatabaseSettings? settings)
        {
            Settings = settings;
        }

        /// <summary>The provider, in skip messages and lock names.</summary>
        public abstract string ProviderName { get; }

        /// <summary>How to configure this provider's test database, for the skip message.</summary>
        protected abstract string ConfigurationHint { get; }

        /// <summary>Registers this provider against the test database, with its options changed by <paramref name="configure"/>.</summary>
        protected abstract void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings, Action<RelationalProviderOptions> configure);

        public async ValueTask InitializeAsync()
        {
            if (Settings == null)
                return;
            _stack = await StartStackAsync(services => AddProvider(services, Settings, _ => { }));
        }

        /// <summary>A second stack on the same database with its options changed by <paramref name="configure"/>.</summary>
        public Task<TestStack> StartStackAsync(Action<RelationalProviderOptions> configure)
        {
            var settings = RequireSettings();
            return StartStackAsync(services => AddProvider(services, settings, configure));
        }

        /// <summary>
        /// Boots AddNucleus plus the provider <paramref name="addProvider"/> registers and
        /// initialises its schema. The fixture's own stack is one; a test that needs the provider
        /// configured differently starts another and disposes it.
        /// </summary>
        protected async Task<TestStack> StartStackAsync(Action<IServiceCollection> addProvider)
        {
            var services = new ServiceCollection();
            var log = new CapturingLoggerProvider();
            services.AddLogging(builder => builder.AddProvider(log));
            services.AddNucleus();
            addProvider(services);
            var built = services.BuildServiceProvider();
            try
            {
                var provider = built.GetRequiredService<IDataProvider>();
                var status = await provider.InitializeAsync();
                if (status != DataProviderStatus.Ok)
                    throw new InvalidOperationException($"{ProviderName} test schema initialization failed: {status}{Environment.NewLine}{string.Join(Environment.NewLine, log.Entries)}");
                return new TestStack(built, provider, new ObjectDataManager(provider, built.GetRequiredService<IObjectSchemaProvider>(), null));
            }
            catch
            {
                built.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            _stack?.Dispose();
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

    /// <summary>A booted Nucleus stack over one provider; disposing it disposes its services.</summary>
    public sealed class TestStack(ServiceProvider services, IDataProvider provider, ObjectDataManager manager) : IDisposable
    {
        public IDataProvider Provider { get; } = provider;
        public ObjectDataManager Manager { get; } = manager;

        public void Dispose() => services.Dispose();
    }

    /// <summary>SQL Server, configured by the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.</summary>
    public sealed class SqlServerFixture() : DatabaseFixture(TestConfig.TryGetSettings())
    {
        public override string ProviderName => "sqlserver";

        protected override string ConfigurationHint => "set the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.";

        protected override void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings, Action<RelationalProviderOptions> configure) =>
            services.AddNucleusSQLDataProvider(options =>
            {
                options.Server = settings.Server;
                options.Database = settings.Database;
                options.Username = settings.Username;
                options.Password = settings.Password;
                configure(options);
            });

        /// <summary>A second stack on the same database with its SQL Server settings changed by <paramref name="configure"/>.</summary>
        public Task<TestStack> StartStackAsync(Action<SQLConfiguration> configure) =>
            StartStackAsync((RelationalProviderOptions options) => configure((SQLConfiguration)options));
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

        protected override void AddProvider(IServiceCollection services, TestConfig.DatabaseSettings settings, Action<RelationalProviderOptions> configure) =>
            services.AddNucleusPostgresDataProvider(options =>
            {
                options.Server = settings.Server;
                options.Database = settings.Database;
                options.Username = settings.Username;
                options.Password = settings.Password;
                configure(options);
            });
    }

    [CollectionDefinition(Name)]
    public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
    {
        public const string Name = "Postgres";
    }
}
