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
            using var stack = await fixture.StartStackAsync(options => options.MaxParametersPerCommand = 60);
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

        /// <summary>
        /// The synchronous save of a mixed batch at the default limit. The save links the update
        /// chain from the insert, and the sync split used to move that chain as one unit, so 1,100
        /// updates of two parameters went out as one command of about 2,200: over SQL Server's
        /// hard limit of 2,100, which failed the save.
        /// </summary>
        [Fact]
        public async Task SyncSaveOfAMixedBatch_SplitsTheUpdateChainToo()
        {
            var manager = fixture.RequireManager();
            const int rows = 1100;
            var prefix = $"mixsync-{Guid.NewGuid():N}-";
            try
            {
                await manager.SaveAsync(Enumerable.Range(0, rows).Select(i => new IntBenchWidget { Name = $"{prefix}old-{i}", Quantity = i }).ToList(), Ct);
                var existing = await manager.Get<IntBenchWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, existing.Count);

                existing.ForEach(w => w.Quantity += 1_000_000);
                manager.Save([.. existing, new IntBenchWidget { Name = $"{prefix}new", Quantity = 7 }]);

                Assert.Equal(rows, await manager.Rows<IntBenchWidget>(w => w.Name!.StartsWith(prefix) && w.Quantity >= 1_000_000).CountAsync(Ct));
                Assert.Equal(1, await manager.Rows<IntBenchWidget>(w => w.Name == $"{prefix}new").CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
            }
        }
    }
}
