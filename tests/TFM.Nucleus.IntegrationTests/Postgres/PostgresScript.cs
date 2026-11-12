using Npgsql;

namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>Runs a statement against the PostgreSQL test database outside Nucleus, for a test's own setup.</summary>
    public static class PostgresScript
    {
        public static async Task ExecuteAsync(TestConfig.DatabaseSettings settings, string sql, CancellationToken token)
        {
            await using var connection = await OpenAsync(settings, token);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        /// <summary>The first column of the first row the statement returns.</summary>
        public static async Task<object?> ScalarAsync(TestConfig.DatabaseSettings settings, string sql, CancellationToken token)
        {
            await using var connection = await OpenAsync(settings, token);
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(token);
            return value is DBNull ? null : value;
        }

        // The server setting may carry a port as host:port, as the provider reads it.
        private static async Task<NpgsqlConnection> OpenAsync(TestConfig.DatabaseSettings settings, CancellationToken token)
        {
            var parts = settings.Server.Split(':');
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = parts[0],
                Port = parts.Length > 1 ? int.Parse(parts[1]) : 5432,
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
}
