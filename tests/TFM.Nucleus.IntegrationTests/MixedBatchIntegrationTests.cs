namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A save of new and existing objects of one type is one table group. Over the parameter
    /// limit that group goes to the bulk engine, which writes its inserts and its updates as two
    /// writes. The limit is lowered so a few rows take the batch past it on every provider.
    /// </summary>
    public abstract class MixedBatchIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task NewAndExistingRowsOverTheLimit_AreAllWritten()
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await fixture.StartStackAsync(options => options.MaxParametersPerQuery = 60);
            var manager = stack.Manager;
            const int rows = 20;
            var prefix = $"mixed-{Guid.NewGuid():N}-";
            try
            {
                await manager.SaveAsync(Enumerable.Range(0, rows).Select(i => new IntBenchWidget { Name = $"{prefix}old-{i}", Quantity = i }).ToList());
                var existing = await manager.Get<IntBenchWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, existing.Count);

                existing.ForEach(w => w.Quantity += 1_000_000);
                var fresh = Enumerable.Range(0, rows).Select(i => new IntBenchWidget { Name = $"{prefix}new-{i}", Quantity = i }).ToList();
                await manager.SaveAsync(existing.Concat(fresh).ToList());

                var saved = await manager.Get<IntBenchWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(2 * rows, saved.Count);
                Assert.All(saved.Where(w => w.Name!.Contains("-old-")), w => Assert.Equal($"{prefix}old-{w.Quantity - 1_000_000}", w.Name));
                Assert.All(saved.Where(w => w.Name!.Contains("-new-")), w => Assert.Equal($"{prefix}new-{w.Quantity}", w.Name));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
            }
        }
    }
}
