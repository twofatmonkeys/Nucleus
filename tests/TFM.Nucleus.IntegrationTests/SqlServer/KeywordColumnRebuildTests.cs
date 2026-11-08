namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// A table whose columns are named with keywords (By, Order) is created with every name
    /// bracketed. Its rebuild copies the rows across, and that copy used to leave the names bare, so
    /// "Incorrect syntax near the keyword 'By'" stopped the provider starting and the table could
    /// never take a new column.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class KeywordColumnRebuildTests(SqlServerFixture fixture)
    {
        private const string Table = "tbl_TFM_Nucleus_IntegrationTests_IntKeywordItem";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task KeywordColumns_SurviveARebuild_WithTheirRows()
        {
            var settings = fixture.RequireSettings();
            fixture.RequireManager();
            var id = Guid.NewGuid();

            // The shared stack made the table current. Take it back to its shape before Note existed,
            // holding one row, so the next start must rebuild it.
            await SqlServerScript.ExecuteAsync(settings,
                $"ALTER TABLE [dbo].[{Table}] DROP COLUMN [Note]; " +
                $"INSERT INTO [dbo].[{Table}] ([ID], [By], [Order], [Timestamp]) VALUES ('{id}', N'Sam', 3, SYSDATETIMEOFFSET());", Ct);
            try
            {
                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                var row = stack.Manager.LoadByID<IntKeywordItem>(id);
                Assert.NotNull(row);
                Assert.Equal("Sam", row!.By);
                Assert.Equal(3, row.Order);
                Assert.Null(row.Note);
            }
            finally
            {
                await SqlServerScript.ExecuteAsync(settings, $"DELETE FROM [dbo].[{Table}] WHERE [ID] = '{id}';", Ct);
            }
        }
    }
}
