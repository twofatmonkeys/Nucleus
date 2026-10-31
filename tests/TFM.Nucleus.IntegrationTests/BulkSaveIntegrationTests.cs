namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A batch with more parameters than the provider binds in one command goes to the
    /// provider's bulk engine. The batch here is sized from the provider's own limit, so every
    /// provider's engine runs: inserts, then updates where each row changes different columns,
    /// which the engine must apply per row.
    /// </summary>
    public abstract class BulkSaveIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task OversizedBatch_InsertsThenUpdatesEachRowsOwnChanges()
        {
            var manager = fixture.RequireManager();
            // An insert binds three values a row and an update two, so this many rows takes both
            // past the limit.
            var rows = fixture.Provider!.Capabilities.MaxParametersPerCommand / 2 + 50;
            var prefix = $"bulk-{Guid.NewGuid():N}-";
            var widgets = Enumerable.Range(0, rows).Select(i => new IntWidget { Name = $"{prefix}{i}", Quantity = i }).ToList();

            await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                await context.BulkSaveAsync(widgets, Ct);

            var saved = await manager.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
            Assert.Equal(rows, saved.Count);
            Assert.All(saved, w => Assert.Equal($"{prefix}{w.Quantity}", w.Name));

            // Even rows change only their quantity, odd rows only their name.
            foreach (var w in saved)
            {
                if (w.Quantity % 2 == 0)
                    w.Quantity += 1_000_000;
                else
                    w.Name += "-renamed";
            }
            await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                await context.BulkSaveAsync(saved, Ct);

            var updated = await manager.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix)).ToListAsync(Ct);
            Assert.Equal(rows, updated.Count);
            Assert.All(updated, w =>
            {
                if (w.Quantity >= 1_000_000)
                    Assert.Equal($"{prefix}{w.Quantity - 1_000_000}", w.Name);
                else
                    Assert.Equal($"{prefix}{w.Quantity}-renamed", w.Name);
            });
            Assert.Equal((rows + 1) / 2, updated.Count(w => w.Quantity >= 1_000_000));
        }
    }
}
