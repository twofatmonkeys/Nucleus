using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// A long list goes to SQL Server as one table-valued parameter only for the element types
    /// that have a list type. A list of any other type still binds a parameter per value, and
    /// past the engine's limit it is refused before the command is sent, by an error that names
    /// the list and the limit rather than the driver's.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class ListParameterLimitTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ALongListWithNoListType_IsRefused_NamingTheLimit()
        {
            IObjectDataManager data = fixture.RequireManager();
            var stamps = Enumerable.Range(0, 3000).Select(i => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i)).ToList();

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => data.Get<IntWidget>().Where(w => stamps.Contains(w.Timestamp)).ToListAsync(Ct));

            var refused = ExceptionChain.Find<InvalidQueryException>(ex);
            Assert.NotNull(refused);
            Assert.Contains("3000 values", refused!.Message);
            Assert.Contains("Timestamp", refused.Message);
        }

        [Fact]
        public async Task AShortListWithNoListType_StillLoads()
        {
            IObjectDataManager data = fixture.RequireManager();
            var widget = new IntWidget { Name = $"lp-{Guid.NewGuid():N}", Quantity = 1 };
            await data.SaveAsync(widget, Ct);
            try
            {
                var loaded = await data.LoadByIDAsync<IntWidget>(widget.ID, Ct);
                var stamps = Enumerable.Range(0, 300).Select(i => loaded!.Timestamp.AddDays(-i)).ToList();

                var found = await data.Get<IntWidget>().Where(w => w.ID == widget.ID && stamps.Contains(w.Timestamp)).ToListAsync(Ct);

                Assert.Equal(widget.ID, Assert.Single(found).ID);
            }
            finally
            {
                await fixture.DeleteWidgetsAsync(widget.Name!, CancellationToken.None);
            }
        }
    }
}
