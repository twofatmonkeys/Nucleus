using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Testing;

/// <summary>
/// The SQLite test database: a file per .NET runtime, at the path
/// <see cref="NucleusTestDatabaseSettings.Database"/> holds with the runtime's major version added
/// before the extension (<c>nucleus.db</c> becomes <c>nucleus.net10.db</c>). The provider creates it
/// when it is missing. Unlike the server databases, the net8.0 and net10.0 runs do not share one:
/// SQLite lets one connection write at a time and does not queue its waiters, so two runs writing
/// one file back to back could starve a writer past its busy timeout. Writers inside one run still
/// contend, which the provider's own tests exercise.
/// </summary>
public sealed class SqliteTestDatabase : NucleusTestDatabase
{
    /// <param name="settings">Where the database is; null when it is not configured.</param>
    public SqliteTestDatabase(NucleusTestDatabaseSettings? settings) : base(settings)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => "sqlite";

    /// <inheritdoc />
    protected override string ConfigurationHint => "set the Nucleus:Sqlite:Database user secret or the NUCLEUS_SQLITE_TEST_DATABASE environment variable to a file path.";

    /// <inheritdoc />
    public override void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqliteDataProvider(options => UseTestFile(options, configure));

    /// <inheritdoc />
    public override void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqliteDataProvider(rootType, options => UseTestFile(options, configure));

    /// <summary>A stack with its SQLite settings changed by <paramref name="configure"/>.</summary>
    public Task<NucleusTestStack> StartStackAsync(Action<TFM.Nucleus.Data.SQLite.SqliteConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return StartStackAsync((RelationalProviderOptions options) => configure((TFM.Nucleus.Data.SQLite.SqliteConfiguration)options));
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public override async Task<object?> ScalarAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqliteCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQLite has no table locks: an IMMEDIATE transaction takes the file's write lock, so every
    /// other writer waits, whatever table it writes, and readers carry on in write-ahead log mode.
    /// </remarks>
    public override async Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default)
    {
        var connection = await OpenAsync(token);
        try
        {
            var transaction = connection.BeginTransaction(deferred: false);
            return new HeldLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>The table's root page: a rename keeps it, a rebuild creates the table anew on another page.</remarks>
    public override string TableIdSql(string table) => $"SELECT rootpage FROM sqlite_schema WHERE type = 'table' AND name = '{table.Replace("'", "''")}'";

    /// <inheritdoc />
    public override string DeleteAllSql(string table) => $"DELETE FROM \"{table}\"";

    /// <inheritdoc />
    /// <remarks>
    /// SQLite refuses to drop an indexed column, so the column's Nucleus indexes are dropped first,
    /// one statement per index and per column.
    /// </remarks>
    public override string DropColumnsSql(string table, params string[] columns) =>
        string.Join("; ", columns.SelectMany(c => new[]
        {
            $"DROP INDEX IF EXISTS \"NCI_{table}_{c}\"",
            $"DROP INDEX IF EXISTS \"UQ_{table}_{c}\"",
            $"ALTER TABLE \"{table}\" DROP COLUMN \"{c}\"",
        }));

    /// <inheritdoc />
    public override string DropTableIfExistsSql(string table) => $"DROP TABLE IF EXISTS \"{table}\"";

    /// <inheritdoc />
    public override string RenameTableSql(string from, string to) => $"ALTER TABLE \"{from}\" RENAME TO \"{to}\"";

    /// <inheritdoc />
    /// <remarks>SQLite gives the copy's columns the affinity of their values, not their declared types.</remarks>
    public override string CopyTableSql(string from, string to) => $"CREATE TABLE \"{to}\" AS SELECT * FROM \"{from}\"";

    /// <inheritdoc />
    public override string RowCountSql(string table) => $"SELECT count(*) FROM \"{table}\"";

    /// <summary>
    /// Points a provider's options at the test file, then applies <paramref name="configure"/>. SQLite
    /// needs the path only, so the server and sign-in settings are left unset.
    /// </summary>
    private void UseTestFile(RelationalProviderOptions options, Action<RelationalProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        options.Database = FilePath;
        configure(options);
    }

    /// <summary>The full path of this runtime's file: the configured path with <c>.net{major}</c> before its extension.</summary>
    public string FilePath
    {
        get
        {
            var path = Path.GetFullPath(RequireSettings().Database);
            var runtime = $".net{Environment.Version.Major}";
            return Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + runtime + Path.GetExtension(path));
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
