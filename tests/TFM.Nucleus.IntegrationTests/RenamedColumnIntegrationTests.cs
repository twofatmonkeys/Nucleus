namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// [DatabaseColumn]'s ColumnName names the physical column on every path: the table, writes,
    /// reads, predicates, child links, unique indexes and the bulk engine. Values still bind to the
    /// entity by property name.
    /// </summary>
    public abstract class RenamedColumnIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        [Fact]
        public async Task Schema_IsCurrentAfterInitialise()
        {
            fixture.RequireManager();
            using var schema = await fixture.SchemaLockAsync();

            Assert.Equal(DataProviderStatus.Ok, await fixture.Provider!.CheckSchemaIsCurrentAsync(null));
        }

        [Fact]
        public async Task SaveAndLoad_RoundTripsEveryRenamedColumn_OnEveryLoadPath()
        {
            var manager = fixture.RequireManager();
            var title = Tag("rt");
            try
            {
                var item = new IntRenamedItem { Title = title, Code = Tag("code"), Count = 7 };
                item.Notes.Add(new IntRenamedNote { Text = "first" });
                item.Notes.Add(new IntRenamedNote { Text = "second" });
                await manager.SaveAsync(item);

                var byId = manager.LoadByID<IntRenamedItem>(item.ID);
                var listed = Assert.Single(manager.Get<IntRenamedItem>().Where(i => i.Title == title).ToList());
                var streamed = Assert.Single(await manager.Get<IntRenamedItem>().Where(i => i.Title == title).ToListAsync(Ct));
                foreach (var loaded in new[] { byId!, listed, streamed })
                {
                    Assert.Equal(item.Code, loaded.Code);
                    Assert.Equal(7, loaded.Count);
                    Assert.Equal(["first", "second"], loaded.Notes.Select(n => n.Text).Order());
                    Assert.False(loaded.HasChanges);
                }

                listed.Count = 8;
                listed.Title = title + "-renamed";
                await manager.SaveAsync(listed);

                var reloaded = Assert.Single(manager.Get<IntRenamedItem>().Where(i => i.Title == title + "-renamed").ToList());
                Assert.Equal(8, reloaded.Count);
                Assert.Equal(2, reloaded.Notes.Count);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRenamedItem>(i => i.Title!.StartsWith(title), Ct);
            }
        }

        [Fact]
        public async Task Predicates_FilterOnTheRenamedColumns()
        {
            var manager = fixture.RequireManager();
            var prefix = Tag("pr");
            try
            {
                await manager.SaveAsync(new List<IntRenamedItem>
                {
                    new() { Title = $"{prefix}-a", Code = $"{prefix}-x", Count = 1 },
                    new() { Title = $"{prefix}-b", Code = $"{prefix}-y", Count = 2 },
                    new() { Title = $"{prefix}-c", Code = $"{prefix}-z", Count = 3 },
                });

                var matched = manager.Get<IntRenamedItem>()
                    .Where(i => i.Title!.StartsWith(prefix) && i.Count >= 2 && i.Code != $"{prefix}-z")
                    .ToList();

                Assert.Equal($"{prefix}-b", Assert.Single(matched).Title);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRenamedItem>(i => i.Title!.StartsWith(prefix), Ct);
            }
        }

        [Fact]
        public async Task DuplicateValue_OnARenamedUniqueColumn_NamesThePropertyAndTheColumn()
        {
            var manager = fixture.RequireManager();
            var title = Tag("uq");
            var code = Tag("code");
            try
            {
                await manager.SaveAsync(new IntRenamedItem { Title = title, Code = code });

                var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntRenamedItem { Title = title, Code = code }));

                var violation = ExceptionChain.Find<UniqueViolationException>(ex);
                Assert.NotNull(violation);
                Assert.Equal(nameof(IntRenamedItem.Code), violation!.PropertyName);
                Assert.Equal("item_code", violation.ColumnName);
                Assert.True(violation.Is<IntRenamedItem>(nameof(IntRenamedItem.Code)));
                Assert.Equal("UQ_tbl_TFM_Nucleus_IntegrationTests_IntRenamedItem_item_code", violation.IndexName);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRenamedItem>(i => i.Title == title, Ct);
            }
        }

        [Fact]
        public async Task BulkSave_WritesAndUpdatesTheRenamedColumns()
        {
            var manager = fixture.RequireManager();
            // An insert binds four values a row (ID, title, code, count), so this many rows takes
            // the batch past the provider's limit and into its bulk engine.
            var rows = fixture.Provider!.Capabilities.MaxParametersPerCommand / 4 + 50;
            var prefix = Tag("bk");
            var items = Enumerable.Range(0, rows).Select(i => new IntRenamedRow { Title = $"{prefix}-{i}", Code = $"{prefix}-c{i}", Count = i }).ToList();
            try
            {
                await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                    await context.BulkSaveAsync(items, Ct);

                var saved = await manager.Get<IntRenamedRow>().Where(i => i.Title!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, saved.Count);
                Assert.All(saved, i => Assert.Equal($"{prefix}-c{i.Count}", i.Code));

                saved.ForEach(i => i.Count += 1_000_000);
                await using (var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct))
                    await context.BulkSaveAsync(saved, Ct);

                var updated = await manager.Get<IntRenamedRow>().Where(i => i.Title!.StartsWith(prefix)).ToListAsync(Ct);
                Assert.Equal(rows, updated.Count);
                Assert.All(updated, i => Assert.Equal($"{prefix}-c{i.Count - 1_000_000}", i.Code));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRenamedRow>(i => i.Title!.StartsWith(prefix), Ct);
            }
        }
    }
}
