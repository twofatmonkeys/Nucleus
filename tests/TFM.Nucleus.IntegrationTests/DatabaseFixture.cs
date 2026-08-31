using Microsoft.Extensions.DependencyInjection;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Boots the full stack (AddNucleus + AddNucleusSQLDataProvider) against the live test
    /// database and initializes the schema once for the collection. When no credentials are
    /// configured the fixture stays unconfigured and each test skips instead of failing, so
    /// `dotnet test` passes on machines without a test database (CI included).
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        public TestConfig.DatabaseSettings? Settings { get; }
        public IDataProvider? Provider { get; private set; }
        public ObjectDataManager? Manager { get; private set; }
        private ServiceProvider? _services;

        public DatabaseFixture()
        {
            Settings = TestConfig.TryGetSettings();
        }

        public async ValueTask InitializeAsync()
        {
            if (Settings == null)
                return;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus();
            services.AddNucleusSQLDataProvider(options =>
            {
                options.Server = Settings.Server;
                options.Database = Settings.Database;
                options.Username = Settings.Username;
                options.Password = Settings.Password;
            });
            _services = services.BuildServiceProvider();

            Provider = _services.GetRequiredService<IDataProvider>();
            var status = await Provider.InitializeAsync();
            if (status != DataProviderStatus.Ok)
                throw new InvalidOperationException($"Test schema initialization failed: {status}");
            Manager = new ObjectDataManager(Provider, _services.GetRequiredService<IObjectSchemaProvider>(), null);
        }

        public ValueTask DisposeAsync()
        {
            _services?.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>Returns the settings, or dynamically skips the calling test when unconfigured.</summary>
        public TestConfig.DatabaseSettings RequireSettings()
        {
            Assert.SkipWhen(Settings == null,
                "Test database not configured - set the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.");
            return Settings!;
        }

        /// <summary>Returns the initialized manager, or dynamically skips when unconfigured.</summary>
        public ObjectDataManager RequireManager()
        {
            RequireSettings();
            return Manager!;
        }
    }

    [CollectionDefinition("Database")]
    public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
    {
    }
}
