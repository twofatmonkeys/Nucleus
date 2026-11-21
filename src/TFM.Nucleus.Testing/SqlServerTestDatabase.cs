using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Testing;

/// <summary>The SQL Server test database. Tables live in <c>dbo</c>.</summary>
public sealed class SqlServerTestDatabase : NucleusTestDatabase
{
    /// <param name="settings">Where the database is; null when it is not configured.</param>
    public SqlServerTestDatabase(NucleusTestDatabaseSettings? settings) : base(settings)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => "sqlserver";

    /// <inheritdoc />
    protected override string ConfigurationHint => "set the Nucleus:* user secrets or NUCLEUS_TEST_* environment variables.";

    /// <inheritdoc />
    public override void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqlServerDataProvider(options => UseTestDatabase(options, configure));

    /// <inheritdoc />
    public override void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqlServerDataProvider(rootType, options => UseTestDatabase(options, configure));

    /// <summary>A stack with its SQL Server settings changed by <paramref name="configure"/>.</summary>
    public Task<NucleusTestStack> StartStackAsync(Action<SQLConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return StartStackAsync((RelationalProviderOptions options) => configure((SQLConfiguration)options));
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public override async Task<object?> ScalarAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc />
    /// <remarks>An exclusive table lock, held to the end of the transaction, which no reader or writer passes.</remarks>
    public override async Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default)
    {
        var connection = await OpenAsync(token);
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            await using var command = new SqlCommand($"SELECT COUNT(*) FROM [dbo].[{table}] WITH (TABLOCKX, HOLDLOCK)", connection, transaction);
            await command.ExecuteScalarAsync(token);
            return new HeldLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public override string TableIdSql(string table) => $"SELECT CAST(OBJECT_ID(N'dbo.{table}') AS bigint)";

    /// <inheritdoc />
    public override string DeleteAllSql(string table) => $"DELETE FROM [dbo].[{table}]";

    /// <inheritdoc />
    /// <remarks>A column cannot be dropped while an index or a default constraint depends on it, so those go first.</remarks>
    public override string DropColumnsSql(string table, params string[] columns) => string.Concat(columns.Select(c =>
        $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'NCI_{table}_{c}' AND object_id = OBJECT_ID(N'dbo.{table}')) DROP INDEX [NCI_{table}_{c}] ON [dbo].[{table}]; " +
        $"IF OBJECT_ID(N'dbo.DF_{table}_{c}', N'D') IS NOT NULL ALTER TABLE [dbo].[{table}] DROP CONSTRAINT [DF_{table}_{c}]; " +
        $"ALTER TABLE [dbo].[{table}] DROP COLUMN [{c}]; "));

    /// <inheritdoc />
    /// <remarks>DROP TABLE IF EXISTS needs a newer compatibility level than a test database may have.</remarks>
    public override string DropTableIfExistsSql(string table) => $"IF OBJECT_ID(N'dbo.{table}', N'U') IS NOT NULL DROP TABLE [dbo].[{table}]";

    /// <inheritdoc />
    public override string RenameTableSql(string from, string to) => $"EXEC sp_rename N'dbo.{from}', N'{to}'";

    /// <inheritdoc />
    public override string CopyTableSql(string from, string to) => $"SELECT * INTO [dbo].[{to}] FROM [dbo].[{from}]";

    /// <inheritdoc />
    public override string RowCountSql(string table) => $"SELECT COUNT_BIG(*) FROM [dbo].[{table}]";

    private async Task<SqlConnection> OpenAsync(CancellationToken token)
    {
        var settings = RequireSettings();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = settings.Database,
            UserID = settings.Username,
            Password = settings.Password,
            TrustServerCertificate = true,
        };
        var connection = new SqlConnection(builder.ConnectionString);
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
