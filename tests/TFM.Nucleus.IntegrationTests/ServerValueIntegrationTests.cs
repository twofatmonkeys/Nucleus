namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A column with SetValueOverride is computed by the server on every write, and one marked
    /// ReturnOutputToProperty is read back into the entity by the write itself, on every write
    /// path: one object, a child collection, the split sync path and the bulk engine. The value in
    /// memory is then the value stored.
    /// </summary>
    public abstract class ServerValueIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        [Fact]
        public async Task SaveAsync_ReadsTheServerValueBack_OnInsertAndUpdate()
        {
            var manager = fixture.RequireManager();
            var name = Tag("sv");
            try
            {
                var before = DateTimeOffset.UtcNow.AddMinutes(-5);
                var item = new IntStampedItem { Name = name };
                item.Lines.Add(new IntStampedLine { Name = "line" });
                await manager.SaveAsync(item);

                Assert.True(item.StampedAt > before, $"StampedAt was not read back: {item.StampedAt:O}");
                Assert.True(item.Lines.Single().StampedAt > before, "The child's StampedAt was not read back.");
                var stored = manager.LoadByID<IntStampedItem>(item.ID)!;
                Assert.Equal(stored.StampedAt, item.StampedAt);
                Assert.Equal(stored.Lines.Single().StampedAt, item.Lines.Single().StampedAt);
                Assert.False(item.HasChanges);

                var inserted = item.StampedAt;
                await Task.Delay(20, Ct);
                item.Name = name + "-updated";
                await manager.SaveAsync(item);

                Assert.True(item.StampedAt > inserted, "The update's StampedAt was not read back.");
                Assert.Equal(manager.LoadByID<IntStampedItem>(item.ID)!.StampedAt, item.StampedAt);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntStampedItem>(i => i.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task Save_ReadsTheServerValueBack_OnTheSplitSyncPath()
        {
            var manager = fixture.RequireManager();
            // ID, name and two server functions a row: this many rows splits into several commands.
            var rows = fixture.Provider!.Capabilities.MaxParametersPerCommand / 4 + 50;
            var prefix = Tag("sp");
            try
            {
                var items = Enumerable.Range(0, rows).Select(i => new IntStampedRow { Name = $"{prefix}-{i}" }).ToList();
                manager.Save(items);

                var stored = manager.Get<IntStampedRow>().Where(r => r.Name!.StartsWith(prefix)).ToList().ToDictionary(r => r.ID);
                Assert.Equal(rows, stored.Count);
                Assert.All(items, i => Assert.Equal(stored[i.ID].StampedAt, i.StampedAt));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntStampedRow>(r => r.Name!.StartsWith(prefix), Ct);
            }
        }

        [Fact]
        public async Task BulkSave_ReadsTheServerValueBack_OnInsertAndUpdate()
        {
            var manager = fixture.RequireManager();
            var rows = fixture.Provider!.Capabilities.MaxParametersPerCommand / 2 + 50;
            var prefix = Tag("bk");
            try
            {
                var items = Enumerable.Range(0, rows).Select(i => new IntStampedRow { Name = $"{prefix}-{i}" }).ToList();
                await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                    await context.BulkSaveAsync(items, Ct);

                var stored = (await manager.Get<IntStampedRow>().Where(r => r.Name!.StartsWith(prefix)).ToListAsync(Ct)).ToDictionary(r => r.ID);
                Assert.Equal(rows, stored.Count);
                Assert.All(items, i => Assert.Equal(stored[i.ID].StampedAt, i.StampedAt));

                var loaded = stored.Values.ToList();
                var inserted = loaded.ToDictionary(r => r.ID, r => r.StampedAt);
                loaded.ForEach(r => r.Name += "-u");
                await Task.Delay(20, Ct);
                await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                    await context.BulkSaveAsync(loaded, Ct);

                var updated = (await manager.Get<IntStampedRow>().Where(r => r.Name!.StartsWith(prefix)).ToListAsync(Ct)).ToDictionary(r => r.ID);
                Assert.All(loaded, r => Assert.True(r.StampedAt > inserted[r.ID], "The bulk update's StampedAt was not read back."));
                Assert.All(loaded, r => Assert.Equal(updated[r.ID].StampedAt, r.StampedAt));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntStampedRow>(r => r.Name!.StartsWith(prefix), Ct);
            }
        }
    }
}
