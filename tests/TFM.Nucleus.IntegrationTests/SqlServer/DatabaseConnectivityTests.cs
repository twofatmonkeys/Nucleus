using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>The SQL Server test database is reachable with the configured credentials, outside Nucleus.</summary>
    [Collection(SqlServerCollection.Name)]
    public class DatabaseConnectivityTests(SqlServerFixture fixture)
    {
        [Fact]
        public async Task CanConnectAndQueryServer()
        {
            var settings = fixture.RequireSettings();

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = settings.Server,
                InitialCatalog = settings.Database,
                UserID = settings.Username,
                Password = settings.Password,
                TrustServerCertificate = true,
                ConnectTimeout = 5,
            };

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, result);
        }
    }
}
