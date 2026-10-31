using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// A table Nucleus does not own may hold a column of a type Nucleus does not map. The schema
    /// check reads every column of the database, and used to fail on such a type before comparing
    /// anything.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class ForeignTableTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ForeignColumnOfAnUnknownType_DoesNotFailInitialisation()
        {
            var settings = fixture.RequireSettings();
            var table = $"foreign_{Guid.NewGuid():N}";
            await ExecuteAsync(settings, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL, [Anything] sql_variant NULL)");
            try
            {
                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
            }
            finally
            {
                await ExecuteAsync(settings, $"DROP TABLE [dbo].[{table}]");
            }
        }

        private static async Task ExecuteAsync(TestConfig.DatabaseSettings settings, string sql)
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
            await connection.OpenAsync(Ct);
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
