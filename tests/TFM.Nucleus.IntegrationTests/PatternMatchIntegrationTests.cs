namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// String predicates mean the same on every provider: Contains on a column without
    /// full-text search is a substring match that ignores case, and a wildcard in a value
    /// matches only itself.
    /// </summary>
    public abstract class PatternMatchIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task WildcardsInAValue_MatchLiterally()
        {
            var manager = fixture.RequireManager();
            var prefix = $"wc-{Guid.NewGuid():N}";
            try
            {
                await manager.SaveAsync(new List<IntBenchWidget>
                {
                    new() { Name = $"{prefix}-50%_off" },
                    new() { Name = $"{prefix}-500 boxes" },
                    new() { Name = $"{prefix}-50Xoff" },
                    new() { Name = $"{prefix}-[a]" },
                    new() { Name = $"{prefix}-a" },
                    new() { Name = $@"{prefix}-c:\temp" },
                });

                async Task<string?> Single(System.Linq.Expressions.Expression<Func<IntBenchWidget, bool>> predicate) =>
                    Assert.Single(await manager.Get<IntBenchWidget>().Where(predicate).ToListAsync(Ct)).Name;

                Assert.Equal($"{prefix}-50%_off", await Single(w => w.Name!.StartsWith($"{prefix}-50%_")));
                Assert.Equal($"{prefix}-50%_off", await Single(w => w.Name!.StartsWith(prefix) && w.Name.EndsWith("%_off")));
                Assert.Equal($"{prefix}-50%_off", await Single(w => w.Name!.StartsWith(prefix) && w.Name.Contains("0%_")));
                Assert.Equal($"{prefix}-[a]", await Single(w => w.Name!.StartsWith($"{prefix}-[a")));
                Assert.Equal($@"{prefix}-c:\temp", await Single(w => w.Name!.StartsWith(prefix) && w.Name.EndsWith(@":\temp")));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
            }
        }

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
