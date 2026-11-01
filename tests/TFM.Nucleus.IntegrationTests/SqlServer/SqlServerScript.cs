using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>Runs a statement against the SQL Server test database outside Nucleus, for a test's own setup.</summary>
    public static class SqlServerScript
    {
        public static async Task ExecuteAsync(TestConfig.DatabaseSettings settings, string sql, CancellationToken token)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = settings.Server,
                InitialCatalog = settings.Database,
                UserID = settings.Username,
                Password = settings.Password,
                TrustServerCertificate = true,
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
