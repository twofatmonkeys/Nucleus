namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// A table whose columns are named with keywords (By, Order) is created with every name
    /// bracketed. Its rebuild copies the rows across, and that copy used to leave the names bare, so
    /// "Incorrect syntax near the keyword 'By'" stopped the provider starting and the table could
    /// never change. A table that only lacks a column has it added in place, which copies nothing,
    /// so this test changes a column's type to make the next start rebuild the table.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class KeywordColumnRebuildTests(SqlServerFixture fixture)
    {
        private const string Table = "tbl_TFM_Nucleus_IntegrationTests_IntKeywordItem";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task KeywordColumns_SurviveARebuild_WithTheirRows()
        {
            fixture.RequireSettings();
            fixture.RequireManager();
            var id = Guid.NewGuid();
            using var schema = await fixture.SchemaLockAsync();

            // The shared stack made the table current. Give Note a type the entity does not have,
            // holding one row, so the next start must rebuild it.
            await fixture.Database.ExecuteAsync(
                $"ALTER TABLE [dbo].[{Table}] ALTER COLUMN [Note] nvarchar(100) NULL; " +
                $"INSERT INTO [dbo].[{Table}] ([ID], [By], [Order], [Note], [Timestamp]) VALUES ('{id}', N'Sam', 3, N'kept', SYSDATETIMEOFFSET());", Ct);
            var tableId = await TableIdAsync();
            try
            {
                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.NotEqual(tableId, await TableIdAsync());
                var row = await stack.Manager.Get<IntKeywordItem>().FindAsync(id, Ct);
                Assert.NotNull(row);
                Assert.Equal("Sam", row!.By);
                Assert.Equal(3, row.Order);
                Assert.Equal("kept", row.Note);
            }
            finally
            {
                await fixture.Database.ExecuteAsync($"DELETE FROM [dbo].[{Table}] WHERE [ID] = '{id}';", Ct);
            }
        }

        private async Task<long> TableIdAsync() =>
            Convert.ToInt64(await fixture.Database.ScalarAsync($"SELECT CAST(OBJECT_ID(N'dbo.{Table}') AS bigint)", Ct));
    }
}
