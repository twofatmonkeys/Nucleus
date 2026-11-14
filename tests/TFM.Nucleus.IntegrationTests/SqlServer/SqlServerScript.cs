using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>Runs a statement against the SQL Server test database outside Nucleus, for a test's own setup.</summary>
    public static class SqlServerScript
    {
        /// <summary>Drops columns from a table, with any index or default constraint that depends on them.</summary>
        public static string DropColumnsSql(string table, params string[] columns) => string.Concat(columns.Select(c =>
            $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'NCI_{table}_{c}' AND object_id = OBJECT_ID(N'dbo.{table}')) DROP INDEX [NCI_{table}_{c}] ON [dbo].[{table}]; " +
            $"IF OBJECT_ID(N'dbo.DF_{table}_{c}', N'D') IS NOT NULL ALTER TABLE [dbo].[{table}] DROP CONSTRAINT [DF_{table}_{c}]; " +
            $"ALTER TABLE [dbo].[{table}] DROP COLUMN [{c}]; "));

        /// <summary>A query returning the table's object id as a bigint, which only a rebuild changes; null when there is no such table.</summary>
        public static string TableIdSql(string table) => $"SELECT CAST(OBJECT_ID(N'dbo.{table}') AS bigint)";

        public static string DeleteAllSql(string table) => $"DELETE FROM [dbo].[{table}]";

        public static async Task ExecuteAsync(TestConfig.DatabaseSettings settings, string sql, CancellationToken token)
        {
            await using var connection = await OpenAsync(settings, token);
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        /// <summary>The first column of the first row the statement returns.</summary>
        public static async Task<object?> ScalarAsync(TestConfig.DatabaseSettings settings, string sql, CancellationToken token)
        {
            await using var connection = await OpenAsync(settings, token);
            await using var command = new SqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(token);
            return value is DBNull ? null : value;
        }

        private static async Task<SqlConnection> OpenAsync(TestConfig.DatabaseSettings settings, CancellationToken token)
        {
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
}
