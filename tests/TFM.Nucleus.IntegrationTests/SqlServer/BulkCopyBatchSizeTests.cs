namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// SQLConfiguration.BulkCopyBatchSize splits the bulk engine's statements. With a batch far
    /// smaller than the rows, the engine sends one statement per batch, and every row still
    /// lands, inserted and then updated.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class BulkCopyBatchSizeTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task SmallBatchSize_WritesEveryRowAcrossStatements()
        {
            fixture.RequireSettings();
            const int batchSize = 97;
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await fixture.StartStackAsync(options => options.BulkCopyBatchSize = batchSize);
            var manager = stack.Manager;
            // Three values a row on insert takes this many rows past the 2,000 parameter limit,
            // into the bulk engine.
            var rows = 1_100;
            var batches = (rows + batchSize - 1) / batchSize;
            var prefix = $"batch-{Guid.NewGuid():N}-";
            var widgets = Enumerable.Range(0, rows).Select(i => new IntBenchWidget { Name = $"{prefix}{i}", Quantity = i }).ToList();
            try
            {
                using (var recorder = new SqlCommandRecorder())
                {
                    await using (var context = await manager.CreateContextAsync<IntBenchWidget>(ContextOptions.Transactional, Ct))
                        await context.BulkSaveAsync(widgets, Ct);
                    Assert.Equal(batches, recorder.Commands.Count(c => c.Contains("s.[_rowid] BETWEEN")));
                }

                var saved = await manager.Get<IntBenchWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, saved.Count);

                saved.ForEach(w => w.Quantity += 1_000_000);
                await using (var context = await manager.CreateContextAsync<IntBenchWidget>(ContextOptions.Transactional, Ct))
                    await context.BulkSaveAsync(saved, Ct);

                var updated = await manager.Get<IntBenchWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, updated.Count);
                Assert.All(updated, w => Assert.Equal($"{prefix}{w.Quantity - 1_000_000}", w.Name));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void BatchSize_BelowOne_IsRefused(int size) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new SQLConfiguration { BulkCopyBatchSize = size });
    }
}
