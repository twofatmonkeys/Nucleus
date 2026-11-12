using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>Runs a statement against the SQL Server test database outside Nucleus, for a test's own setup.</summary>
    public static class SqlServerScript
    {
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
