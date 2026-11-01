using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// SQL Server refuses an OUTPUT clause without INTO on a table with an enabled trigger
    /// (error 334). A write that reads a value back on such a table fails with a message that
    /// names the cause and the way out, with the engine's error inside.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class OutputTriggerTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ReadBack_OnATableWithATrigger_ExplainsTheRefusal()
        {
            var settings = fixture.RequireSettings();
            var manager = fixture.RequireManager();
            const string table = "tbl_TFM_Nucleus_IntegrationTests_IntTriggeredRow";
            var trigger = $"trg_{Guid.NewGuid():N}";
            using var gate = await fixture.LockAsync("output-trigger");
            await SqlServerScript.ExecuteAsync(settings, $"CREATE TRIGGER [dbo].[{trigger}] ON [dbo].[{table}] AFTER INSERT AS BEGIN SET NOCOUNT ON; END", Ct);
            try
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntTriggeredRow { Name = "triggered" }));

                var explained = ExceptionChain.Find<SqlDataProviderException>(ex);
                Assert.NotNull(explained);
                Assert.Contains("ReturnOutputToProperty", explained!.Message);
                Assert.Contains("trigger", explained.Message);
                Assert.Equal(334, ExceptionChain.Find<SqlException>(explained)!.Number);
            }
            finally
            {
                await SqlServerScript.ExecuteAsync(settings, $"DROP TRIGGER [dbo].[{trigger}]", Ct);
            }
        }
    }
}
