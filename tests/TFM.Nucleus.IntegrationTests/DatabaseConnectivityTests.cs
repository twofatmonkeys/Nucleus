using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests
{
    [Collection("Database")]
    public class DatabaseConnectivityTests(DatabaseFixture fixture)
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
