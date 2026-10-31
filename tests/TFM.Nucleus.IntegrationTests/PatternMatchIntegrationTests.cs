namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// String predicates mean the same on every provider: Contains on a column without
    /// full-text search is a substring match that ignores case.
    /// </summary>
    public abstract class PatternMatchIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Contains_OnAPlainColumn_MatchesASubstring_IgnoringCase()
        {
            var manager = fixture.RequireManager();
            var prefix = $"pm-{Guid.NewGuid():N}";
            try
            {
                await manager.SaveAsync(new List<IntBenchWidget>
                {
                    new() { Name = $"{prefix}-alphaMIDDLEomega" },
                    new() { Name = $"{prefix}-nothing" },
                });

                var matched = await manager.Get<IntBenchWidget>()
                    .Where(w => w.Name!.StartsWith(prefix) && w.Name.Contains("middle"))
                    .ToListAsync(Ct);

                Assert.Equal($"{prefix}-alphaMIDDLEomega", Assert.Single(matched).Name);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
            }
        }
    }
}
