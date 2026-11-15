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

    /// <inheritdoc />
    public override void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusPostgresDataProvider(options => UseTestDatabase(options, configure));

    /// <inheritdoc />
    public override void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusPostgresDataProvider(rootType, options => UseTestDatabase(options, configure));

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
