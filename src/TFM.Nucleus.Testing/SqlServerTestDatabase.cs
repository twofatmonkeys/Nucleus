using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Testing;

/// <summary>
/// The SQL Server test database, one per .NET runtime (<see cref="NucleusTestDatabase.DatabaseName"/>).
/// Tables live in <c>dbo</c>. A missing one is created here, not by the provider, so that it runs at
/// <see cref="MinimumCompatibilityLevel"/>; one that exists is left as it is.
/// </summary>
public sealed class SqlServerTestDatabase : NucleusTestDatabase
{
    /// <param name="settings">Where the database is; null when it is not configured.</param>
    public SqlServerTestDatabase(NucleusTestDatabaseSettings? settings) : base(settings)
    {
    }

    /// <summary>
    /// The compatibility level a database created for the tests is given: the lowest Nucleus
    /// supports. A new server would otherwise create it at its own, newest level, and the suite
    /// would stop catching a statement an older level refuses.
    /// </summary>
    public const int MinimumCompatibilityLevel = 100;

    private Task? _prepared;

    /// <inheritdoc />
    public override string ProviderName => "sqlserver";

    /// <inheritdoc />
    /// <remarks>Creates this runtime's database when it is missing, once per process.</remarks>
    protected override Task PrepareDatabaseAsync() => LazyInitializer.EnsureInitialized(ref _prepared, CreateIfMissingAsync);

    /// <summary>
    /// Creates the database at <see cref="MinimumCompatibilityLevel"/> with READ_COMMITTED_SNAPSHOT
    /// on, as the provider sets it for a database it creates. Another test process may create it at
    /// the same moment; the one that loses finds it there (error 1801) and carries on.
    /// </summary>
    private async Task CreateIfMissingAsync()
    {
        var name = DatabaseName;
        var quoted = $"[{name.Replace("]", "]]")}]";
        await using var connection = await OpenAsync("master", CancellationToken.None);
        await using (var exists = new SqlCommand("SELECT DB_ID(@name)", connection))
        {
            exists.Parameters.AddWithValue("@name", name);
            if (await exists.ExecuteScalarAsync() is not (null or DBNull))
                return;
        }
        try
        {
            await using var create = new SqlCommand($"CREATE DATABASE {quoted}", connection);
            await create.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            return;
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"The SQL Server test database '{name}' does not exist and could not be created: {ex.Message} " +
                $"Create it at compatibility level {MinimumCompatibilityLevel}, or let the test login create databases.", ex);
        }
        await using var settle = new SqlCommand(
            $"ALTER DATABASE {quoted} SET COMPATIBILITY_LEVEL = {MinimumCompatibilityLevel}; " +
            $"ALTER DATABASE {quoted} SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT", connection);
        await settle.ExecuteNonQueryAsync();
    }

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
    /// <remarks>
    /// A schema modification lock, held to the end of the transaction, which no reader or writer
    /// passes. An exclusive table lock is not enough: with READ_COMMITTED_SNAPSHOT on, a reader
    /// takes no shared lock and reads the last committed version past it. The table option the
    /// statement sets is rolled back with the transaction.
    /// </remarks>
    public override async Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default)
    {
        var connection = await OpenAsync(token);
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            await using var command = new SqlCommand($"ALTER TABLE [dbo].[{table}] SET (LOCK_ESCALATION = AUTO)", connection, transaction);
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
    public override string TableIdSql(string table) => $"SELECT CAST(OBJECT_ID(N'dbo.{table}') AS bigint)";

    /// <inheritdoc />
    public override string DeleteAllSql(string table) => $"DELETE FROM [dbo].[{table}]";

    /// <inheritdoc />
    /// <remarks>
    /// A column cannot be dropped while a foreign key, an index or a default constraint depends on
    /// it, so those go first. The keys on the column are found in the catalog, whatever their names,
    /// and dropped by dynamic SQL, with a variable per column so one batch declares each once.
    /// </remarks>
    public override string DropColumnsSql(string table, params string[] columns) => string.Concat(columns.Select((c, i) =>
        $"DECLARE @fk{i} nvarchar(max); SET @fk{i} = N''; " +
        $"SELECT @fk{i} = @fk{i} + N'ALTER TABLE [dbo].[{table}] DROP CONSTRAINT ' + QUOTENAME(fk.name) + N'; ' " +
        "FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = fk.object_id " +
        $"WHERE fk.parent_object_id = OBJECT_ID(N'dbo.{table}') AND COL_NAME(fc.parent_object_id, fc.parent_column_id) = N'{c}'; " +
        $"IF @fk{i} <> N'' EXEC (@fk{i}); " +
        // Every index the column is in, as a key or as a column the index carries: the soft-delete
        // flag is carried by every index of its table.
        $"DECLARE @ix{i} nvarchar(max); SET @ix{i} = N''; " +
        $"SELECT @ix{i} = @ix{i} + N'DROP INDEX ' + QUOTENAME(ix.name) + N' ON [dbo].[{table}]; ' " +
        "FROM sys.indexes ix JOIN sys.index_columns ic ON ic.object_id = ix.object_id AND ic.index_id = ix.index_id " +
        $"WHERE ix.object_id = OBJECT_ID(N'dbo.{table}') AND ix.type = 2 AND ix.is_primary_key = 0 AND COL_NAME(ic.object_id, ic.column_id) = N'{c}'; " +
        $"IF @ix{i} <> N'' EXEC (@ix{i}); " +
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

    /// <summary>A connection to this runtime's database, which is created first when it is missing.</summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken token)
    {
        await PrepareDatabaseAsync();
        return await OpenAsync(DatabaseName, token);
    }

    private async Task<SqlConnection> OpenAsync(string database, CancellationToken token)
    {
        var settings = RequireSettings();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = database,
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
