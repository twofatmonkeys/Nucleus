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
            await SqlServerScript.ExecuteAsync(settings, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL, [Anything] sql_variant NULL)", Ct);
            try
            {
                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
            }
            finally
            {
                await SqlServerScript.ExecuteAsync(settings, $"DROP TABLE [dbo].[{table}]", Ct);
            }
        }
    }
}
