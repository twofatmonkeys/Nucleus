using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Testing;

/// <summary>
/// One provider's integration-test database: whether it is configured, how to boot a Nucleus
/// stack on it, the cross-process locks over it, and the raw statements a test uses to set a table
/// up by hand. <see cref="SqlServer"/>, <see cref="Postgres"/> and <see cref="Sqlite"/> are the ones there are.
/// </summary>
/// <remarks>
/// Framework neutral on purpose: nothing here skips a test. A framework shim checks
/// <see cref="IsConfigured"/> and skips with <see cref="SkipReason"/> its own way, so the same
/// package serves xunit v2, xunit v3 and MSTest suites.
/// </remarks>
public abstract class NucleusTestDatabase
{
    private static readonly Lazy<SqlServerTestDatabase> SqlServerDatabase = new(() => new SqlServerTestDatabase(NucleusTestDatabaseSettings.TryGetSqlServer()));
    private static readonly Lazy<PostgresTestDatabase> PostgresDatabase = new(() => new PostgresTestDatabase(NucleusTestDatabaseSettings.TryGetPostgres()));
    private static readonly Lazy<SqliteTestDatabase> SqliteDatabase = new(() => new SqliteTestDatabase(NucleusTestDatabaseSettings.TryGetSqlite()));

    /// <summary>The SQL Server test database, from <see cref="NucleusTestDatabaseSettings.TryGetSqlServer"/>.</summary>
    public static SqlServerTestDatabase SqlServer => SqlServerDatabase.Value;

    /// <summary>The PostgreSQL test database, from <see cref="NucleusTestDatabaseSettings.TryGetPostgres"/>.</summary>
    public static PostgresTestDatabase Postgres => PostgresDatabase.Value;

    /// <summary>The SQLite test database, from <see cref="NucleusTestDatabaseSettings.TryGetSqlite"/>.</summary>
    public static SqliteTestDatabase Sqlite => SqliteDatabase.Value;

    /// <param name="settings">Where the database is; null when it is not configured.</param>
    protected NucleusTestDatabase(NucleusTestDatabaseSettings? settings)
    {
        Settings = settings;
    }

    /// <summary>Where the database is; null when it is not configured.</summary>
    public NucleusTestDatabaseSettings? Settings { get; }

    /// <summary>True when <see cref="Settings"/> is complete, so the database tests can run.</summary>
    public bool IsConfigured => Settings != null;

    /// <summary>The provider's short name, in skip messages and lock names: <c>sqlserver</c>, <c>postgres</c> or <c>sqlite</c>.</summary>
    public abstract string ProviderName { get; }

    /// <summary>How to configure this provider's test database, for the skip message.</summary>
    protected abstract string ConfigurationHint { get; }

    /// <summary>Why a test on this database cannot run, for the framework's skip; null when it can.</summary>
    public string? SkipReason => IsConfigured ? null : $"{ProviderName} test database not configured - {ConfigurationHint}";

    /// <summary>The settings, or throws when the database is not configured. A shim skips before it gets here.</summary>
    /// <exception cref="InvalidOperationException">The database is not configured.</exception>
    public NucleusTestDatabaseSettings RequireSettings() => Settings ?? throw new InvalidOperationException(SkipReason);

    /// <summary>Registers this provider against the test database, with its options changed by <paramref name="configure"/>.</summary>
    public abstract void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure);

    /// <summary>Registers this provider rooted at <paramref name="rootType"/>, so it stores only the entities under that root.</summary>
    public abstract void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure);

    /// <summary>
    /// Boots AddNucleus plus this provider and initialises the schema, which creates, migrates and
    /// renames the tables of every entity Nucleus finds. Hold <see cref="SchemaLockAsync"/> around
    /// it when another process may be changing the same database's schema.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is not configured, or the schema initialisation failed; the message carries what the stack logged.</exception>
    public Task<NucleusTestStack> StartStackAsync(Action<RelationalProviderOptions>? configure = null)
    {
        RequireSettings();
        return StartStackAsync(services => AddProvider(services, configure ?? (_ => { })));
    }

    /// <summary>
    /// Starts a stack while holding <see cref="SchemaLockAsync"/>, the way a fixture boots the
    /// stack its suites share, so the net8.0 and net10.0 runs never initialise one schema at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is not configured, or the schema initialisation failed.</exception>
    public async Task<NucleusTestStack> StartSharedStackAsync(CancellationToken token = default)
    {
        RequireSettings();
        using var schema = await SchemaLockAsync(token);
        return await StartStackAsync();
    }

    /// <summary>
    /// A stack whose only provider is rooted at <paramref name="rootType"/>, so its options (a
    /// naming policy, say) touch no table but those of the entities under that root.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is not configured, or the schema initialisation failed.</exception>
    public Task<NucleusTestStack> StartRootedStackAsync(Type rootType, Action<RelationalProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        RequireSettings();
        return StartStackAsync(services => AddRootedProvider(services, rootType, configure ?? (_ => { })));
    }

    /// <summary>Boots AddNucleus plus the provider <paramref name="addProvider"/> registers and initialises its schema.</summary>
    protected async Task<NucleusTestStack> StartStackAsync(Action<IServiceCollection> addProvider)
    {
        ArgumentNullException.ThrowIfNull(addProvider);
        var services = new ServiceCollection();
        var log = new CapturingLoggerProvider();
        services.AddLogging(builder => builder.AddProvider(log));
        services.AddNucleus();
        addProvider(services);
        // The stack's manager is resolvable as the interface production code takes. It is built
        // here, not by AddNucleusData, so that a stack does not publish the process-wide ambient
        // runtime: several stacks live in one test process, and the last to start would take it.
        services.AddSingleton<IObjectDataManager>(sp =>
            new ObjectDataManager(sp.GetRequiredService<IDataProvider>(), sp.GetRequiredService<IObjectSchemaProvider>(), null));
        var built = services.BuildServiceProvider();
        try
        {
            var provider = built.GetRequiredService<IDataProvider>();
            var status = await provider.InitializeAsync();
            if (status != DataProviderStatus.Ok)
                throw new InvalidOperationException($"{ProviderName} test schema initialization failed: {status}{Environment.NewLine}{string.Join(Environment.NewLine, log.Entries)}");
            return new NucleusTestStack(built, provider, (ObjectDataManager)built.GetRequiredService<IObjectDataManager>(), log);
        }
        catch
        {
            built.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The cross-process lock of a suite that resets its tables, per provider: the net8.0 and
    /// net10.0 runs share a database, but two providers never do.
    /// </summary>
    public Task<NucleusTestLock> LockAsync(string suite, CancellationToken token = default) =>
        NucleusTestLock.AcquireAsync($"{suite}-{ProviderName}", token);

    /// <summary>
    /// The cross-process lock over the database's schema, per provider. Hold it to change a
    /// table's shape with raw DDL, to start a stack (which updates the schema), or to check the
    /// whole schema is current. It is not reentrant: take it once per test, around the whole change.
    /// </summary>
    public Task<NucleusTestLock> SchemaLockAsync(CancellationToken token = default) => LockAsync("schema", token);

    /// <summary>Runs a statement against the test database outside Nucleus, for a test's own setup.</summary>
    public abstract Task ExecuteAsync(string sql, CancellationToken token = default);

    /// <summary>The first column of the first row the statement returns; null for no row or a database null.</summary>
    public abstract Task<object?> ScalarAsync(string sql, CancellationToken token = default);

    /// <summary>
    /// Takes a lock outside Nucleus that keeps every other connection from writing
    /// <paramref name="table"/>, and holds it until the result is disposed, which rolls the
    /// transaction holding it back. On the server engines no other connection can read the table
    /// either; SQLite locks the whole file for writing and lets readers through. For a test of what
    /// a statement that cannot get its lock is told. Lock a table no other suite uses, or the
    /// suites that use it wait too. A database class of its own that does not override it throws
    /// <see cref="NotSupportedException"/>, so one written before it existed still compiles.
    /// </summary>
    public virtual Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default) =>
        throw new NotSupportedException($"{GetType().Name} does not hold locks.");

    /// <summary>A lock taken by <see cref="HoldWriteLockAsync"/>: its connection and the transaction that holds it.</summary>
    protected sealed class HeldLock(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>A query returning the table's object id as a bigint, which only a rebuild changes; null when there is no such table.</summary>
    public abstract string TableIdSql(string table);

    /// <summary>Deletes every row of the table.</summary>
    public abstract string DeleteAllSql(string table);

    /// <summary>Drops columns from a table, with whatever index or default depends on them.</summary>
    public abstract string DropColumnsSql(string table, params string[] columns);

    /// <summary>Drops the table when it exists.</summary>
    public abstract string DropTableIfExistsSql(string table);

    /// <summary>Renames a table by hand, leaving its indexes and constraints with the names they had.</summary>
    public abstract string RenameTableSql(string from, string to);

    /// <summary>A copy of a table's rows in a new table, without its indexes or constraints.</summary>
    public abstract string CopyTableSql(string from, string to);

    /// <summary>A query returning the table's row count.</summary>
    public abstract string RowCountSql(string table);

    /// <summary>
    /// Puts each entity's table back under the first of its <c>[LegacyTableNames]</c>, as a site
    /// still on the old name would have it, dropping any table already under that name first. The
    /// next stack started adopts the tables again. Hold <see cref="SchemaLockAsync"/>, and the
    /// lock of every suite that uses these tables, around the whole round trip.
    /// </summary>
    /// <exception cref="ArgumentException">An entity declares no legacy table name.</exception>
    public async Task MoveToLegacyNamesAsync(IEnumerable<Type> entities, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        foreach (var entity in entities)
        {
            var table = NucleusEntityTable.For(entity);
            var legacy = table.LegacyTableNames.FirstOrDefault()
                ?? throw new ArgumentException($"{entity.FullName} declares no [LegacyTableNames].", nameof(entities));
            await ExecuteAsync(DropTableIfExistsSql(legacy), token);
            await ExecuteAsync(RenameTableSql(table.TableName, legacy), token);
        }
    }

    /// <summary>The table's object id, or null when there is no such table.</summary>
    public async Task<long?> TableIdAsync(string table, CancellationToken token = default) =>
        await ScalarAsync(TableIdSql(table), token) is { } id ? Convert.ToInt64(id) : null;

    /// <summary>The table's row count.</summary>
    public async Task<long> RowCountAsync(string table, CancellationToken token = default) =>
        Convert.ToInt64(await ScalarAsync(RowCountSql(table), token));

    /// <summary>Points a provider's options at the test database, then applies <paramref name="configure"/>.</summary>
    protected void UseTestDatabase(RelationalProviderOptions options, Action<RelationalProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        var settings = RequireSettings();
        options.Server = settings.Server;
        options.Database = settings.Database;
        options.Username = settings.Username;
        options.Password = settings.Password;
        configure(options);
    }
}
