using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Testing;

/// <summary>The PostgreSQL test database. The provider's tables live in <c>public</c>; the server is <c>host</c> or <c>host:port</c>.</summary>
public sealed class PostgresTestDatabase : NucleusTestDatabase
{
    /// <param name="settings">Where the database is; null when it is not configured.</param>
    public PostgresTestDatabase(NucleusTestDatabaseSettings? settings) : base(settings)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => "postgres";

    /// <inheritdoc />
    protected override string ConfigurationHint => "set the Nucleus:Postgres:* user secrets or NUCLEUS_PG_TEST_* environment variables.";

    /// <summary>
    /// The connection limit of every provider this database registers, unless the test's own
    /// <c>configure</c> sets another. At the default of 100, one test process could take every
    /// connection PostgreSQL's default <c>max_connections</c> of 100 allows. The net8.0 and
    /// net10.0 runs share the server, and a store's suite failed with 53300 "too many clients"
    /// whenever their PostgreSQL tests overlapped (the provider then kept two pools of the limit,
    /// which made it worse). At 20, a provider holds at most 20 connections, its change
    /// subscriptions included, so two runs' shared stacks leave room for the stacks a test starts
    /// of its own, the setup statements and a tool.
    /// </summary>
    private const int TestPoolSize = 20;

    /// <inheritdoc />
    public override void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusPostgresDataProvider(options => UseTestDatabase(options, WithTestPool(configure)));

    /// <inheritdoc />
    public override void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusPostgresDataProvider(rootType, options => UseTestDatabase(options, WithTestPool(configure)));

    /// <summary>Caps the pool at <see cref="TestPoolSize"/>, then applies the test's own options, which may change it.</summary>
    private static Action<RelationalProviderOptions> WithTestPool(Action<RelationalProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return options =>
        {
            options.MaxConnectionPoolSize = TestPoolSize;
            configure(options);
        };
    }

    /// <summary>A stack with its PostgreSQL settings changed by <paramref name="configure"/>.</summary>
    public Task<NucleusTestStack> StartStackAsync(Action<TFM.Nucleus.Data.PostgreSQL.PostgresConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return StartStackAsync((RelationalProviderOptions options) => configure((TFM.Nucleus.Data.PostgreSQL.PostgresConfiguration)options));
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public override async Task<object?> ScalarAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc />
    /// <remarks>ACCESS EXCLUSIVE, the one mode that conflicts with every other, readers included.</remarks>
    public override async Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default)
    {
        var connection = await OpenAsync(token);
        try
        {
            var transaction = await connection.BeginTransactionAsync(token);
            await using var command = new NpgsqlCommand($"LOCK TABLE \"public\".\"{table}\" IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            return new HeldLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public override string TableIdSql(string table) => $"SELECT to_regclass('\"public\".\"{table}\"')::oid::bigint";

    /// <inheritdoc />
    public override string DeleteAllSql(string table) => $"DELETE FROM \"public\".\"{table}\"";

    /// <inheritdoc />
    /// <remarks>Dropping a column drops its indexes with it.</remarks>
    public override string DropColumnsSql(string table, params string[] columns) =>
        $"ALTER TABLE \"public\".\"{table}\" {string.Join(", ", columns.Select(c => $"DROP COLUMN \"{c}\""))}";

    /// <inheritdoc />
    public override string DropTableIfExistsSql(string table) => $"DROP TABLE IF EXISTS \"public\".\"{table}\"";

    /// <inheritdoc />
    public override string RenameTableSql(string from, string to) => $"ALTER TABLE \"public\".\"{from}\" RENAME TO \"{to}\"";

    /// <inheritdoc />
    public override string CopyTableSql(string from, string to) => $"CREATE TABLE \"public\".\"{to}\" AS TABLE \"public\".\"{from}\"";

    /// <inheritdoc />
    public override string RowCountSql(string table) => $"SELECT count(*) FROM \"public\".\"{table}\"";

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var settings = RequireSettings();
        var parts = settings.Server.Split(':');
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = parts[0],
            Port = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 5432,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
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
