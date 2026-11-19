using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// The xunit v3 shim over one <see cref="NucleusTestDatabase"/> from TFM.Nucleus.Testing: boots
    /// the full stack once for the collection and turns "not configured" into a dynamic skip, so
    /// `dotnet test` passes on machines without a test database (CI included). Every neutral suite
    /// is an abstract class run once per provider by a sealed subclass in that provider's collection.
    /// </summary>
    public abstract class DatabaseFixture(NucleusTestDatabase database) : IAsyncLifetime
    {
        private NucleusTestStack? _stack;

        /// <summary>The provider's test database: settings, stacks, locks and raw statements.</summary>
        public NucleusTestDatabase Database { get; } = database;

        public NucleusTestDatabaseSettings? Settings => Database.Settings;
        public IDataProvider? Provider => _stack?.Provider;
        public ObjectDataManager? Manager => _stack?.Manager;

        /// <summary>The provider, in skip messages and lock names.</summary>
        public string ProviderName => Database.ProviderName;

        public async ValueTask InitializeAsync()
        {
            if (Database.IsConfigured)
                _stack = await Database.StartSharedStackAsync();
        }

        /// <summary>A second stack on the same database with its options changed by <paramref name="configure"/>.</summary>
        public Task<NucleusTestStack> StartStackAsync(Action<RelationalProviderOptions> configure)
        {
            RequireSettings();
            return Database.StartStackAsync(configure);
        }

        /// <summary>
        /// A second stack on the same database whose only provider is rooted at
        /// <paramref name="rootType"/>, so its options (a naming policy, say) touch no table but
        /// those of the entities under that root.
        /// </summary>
        public Task<NucleusTestStack> StartRootedStackAsync(Type rootType, Action<RelationalProviderOptions> configure)
        {
            RequireSettings();
            return Database.StartRootedStackAsync(rootType, configure);
        }

        public ValueTask DisposeAsync()
        {
            _stack?.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        /// <summary>Returns the settings, or dynamically skips the calling test when unconfigured.</summary>
        public NucleusTestDatabaseSettings RequireSettings()
        {
            Assert.SkipWhen(!Database.IsConfigured, Database.SkipReason ?? "");
            return Database.RequireSettings();
        }

        /// <summary>Returns the initialized manager, or dynamically skips when unconfigured.</summary>
        public ObjectDataManager RequireManager()
        {
            RequireSettings();
            return Manager!;
        }

        /// <summary>The cross-process lock of a suite that resets its tables, per provider.</summary>
        internal Task<NucleusTestLock> LockAsync(string suite) => Database.LockAsync(suite);

        /// <summary>The cross-process lock over the database's schema, per provider. Not reentrant.</summary>
        internal Task<NucleusTestLock> SchemaLockAsync() => Database.SchemaLockAsync();
    }

    /// <summary>SQL Server, configured by the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.</summary>
    public sealed class SqlServerFixture() : DatabaseFixture(NucleusTestDatabase.SqlServer)
    {
        /// <summary>The SQL Server test database, typed for its SQL Server-only members.</summary>
        public SqlServerTestDatabase SqlServer => NucleusTestDatabase.SqlServer;

        /// <summary>A second stack on the same database with its SQL Server settings changed by <paramref name="configure"/>.</summary>
        public Task<NucleusTestStack> StartStackAsync(Action<SQLConfiguration> configure)
        {
            RequireSettings();
            return SqlServer.StartStackAsync(configure);
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
    {
        public const string Name = "SqlServer";
    }

    /// <summary>PostgreSQL, configured by the Nucleus:Postgres:* user secrets or NUCLEUS_PG_TEST_* environment variables.</summary>
    public sealed class PostgresFixture() : DatabaseFixture(NucleusTestDatabase.Postgres);

    [CollectionDefinition(Name)]
    public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
    {
        public const string Name = "Postgres";
    }

    /// <summary>SQLite, configured by the Nucleus:Sqlite:Database user secret or NUCLEUS_SQLITE_TEST_DATABASE, a file path.</summary>
    public sealed class SqliteFixture() : DatabaseFixture(NucleusTestDatabase.Sqlite);

    [CollectionDefinition(Name)]
    public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>
    {
        public const string Name = "Sqlite";
    }
}
