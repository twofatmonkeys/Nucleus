namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A loaded parent saves its changed children on every save path. A child collection builds
    /// its writes through its parent's data provider, which only the manager's SaveAsync used to
    /// set, so every other path threw for a loaded parent. A child with no changes of its own is
    /// not written, so its Timestamp is left alone, while its changed children still are.
    /// </summary>
    public abstract class ChildSaveIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        public enum SavePath
        {
            ManagerSave,
            ContextSave,
            ContextSaveAsync,
            ContextBulkSave,
            ContextBulkSaveAsync,
        }

        private async Task SaveAsync(ObjectDataManager manager, IntBundle bundle, SavePath path)
        {
            if (path == SavePath.ManagerSave)
            {
                manager.Save(bundle);
                return;
            }
            await using var context = await manager.Providers.AllProviders[0].CreateContextAsync(ContextOptions.Transactional, Ct);
            switch (path)
            {
                case SavePath.ContextSave: context.Save(bundle); break;
                case SavePath.ContextSaveAsync: await context.SaveAsync(bundle, Ct); break;
                case SavePath.ContextBulkSave: context.Save((IEnumerable<ICoreObject?>)[bundle]); break;
                case SavePath.ContextBulkSaveAsync: await context.SaveAsync((IEnumerable<ICoreObject?>)[bundle], Ct); break;
            }
        }

        [Theory]
        [InlineData(SavePath.ManagerSave)]
        [InlineData(SavePath.ContextSave)]
        [InlineData(SavePath.ContextSaveAsync)]
        [InlineData(SavePath.ContextBulkSave)]
        [InlineData(SavePath.ContextBulkSaveAsync)]
        public async Task LoadedParent_SavesItsChildChanges(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("cs");
            var bundle = new IntBundle { Name = name };
            bundle.Files.Add(new IntBundleFile { Name = $"{name}-0", Content = "first" });
            await manager.SaveAsync(bundle);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntBundle>().Where(b => b.Name == name).ToListAsync(Ct));
                loaded.Files.Single().Content = "changed";
                loaded.Files.Add(new IntBundleFile { Name = $"{name}-1", Content = "added" });

                await SaveAsync(manager, loaded, path);

                var reloaded = Assert.Single(await manager.Get<IntBundle>().Where(b => b.Name == name).ToListAsync(Ct));
                Assert.Equal(["changed", "added"], reloaded.Files.OrderBy(f => f.Name).Select(f => f.Content));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBundle>(b => b.Name == name, Ct);
            }
        }

        // ---- Only rows with changes of their own are written ----

        private async Task SaveAsync(ObjectDataManager manager, IntRoster roster, SavePath path)
        {
            if (path == SavePath.ManagerSave)
            {
                manager.Save(roster);
                return;
            }
            await using (var context = await manager.Providers.AllProviders[0].CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                switch (path)
                {
                    case SavePath.ContextSave: context.Save(roster); break;
                    case SavePath.ContextSaveAsync: await context.SaveAsync(roster, Ct); break;
                    case SavePath.ContextBulkSave: context.Save((IEnumerable<ICoreObject?>)[roster]); break;
                    case SavePath.ContextBulkSaveAsync: await context.SaveAsync((IEnumerable<ICoreObject?>)[roster], Ct); break;
                }
                await context.CommitAsync(Ct);
            }
        }

        /// <summary>A stored roster of two lines, each with one break, loaded back.</summary>
        private static async Task<IntRoster> SavedRosterAsync(ObjectDataManager manager, string name)
        {
            var roster = new IntRoster { Name = name };
            foreach (var code in new[] { $"{name}-a", $"{name}-b" })
            {
                var line = new IntRosterLine { Code = code, Hours = 8 };
                roster.Lines.Add(line);
                line.Breaks.Add(new IntRosterBreak { Minutes = 30 });
            }
            await manager.SaveAsync(roster, Ct);
            return await LoadRosterAsync(manager, name);
        }

        private static async Task<IntRoster> LoadRosterAsync(ObjectDataManager manager, string name) =>
            Assert.Single(await manager.Get<IntRoster>().Where(r => r.Name == name).ToListAsync(Ct));

        /// <summary>The stored Timestamp of the roster, each line and each break, by ID.</summary>
        private static Dictionary<Guid, DateTimeOffset> Stamps(IntRoster roster)
        {
            var stamps = new Dictionary<Guid, DateTimeOffset> { [roster.ID] = roster.Timestamp };
            foreach (var line in roster.Lines)
            {
                stamps[line.ID] = line.Timestamp;
                foreach (var pause in line.Breaks)
                    stamps[pause.ID] = pause.Timestamp;
            }
            return stamps;
        }

        /// <summary>Long enough for the server clock to move between two writes.</summary>
        private static Task ClockTickAsync() => Task.Delay(50, Ct);

        [Theory]
        [InlineData(SavePath.ManagerSave)]
        [InlineData(SavePath.ContextSave)]
        [InlineData(SavePath.ContextSaveAsync)]
        [InlineData(SavePath.ContextBulkSave)]
        [InlineData(SavePath.ContextBulkSaveAsync)]
        public async Task ParentSave_LeavesUnchangedChildrenUnwritten(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("uc");
            try
            {
                var loaded = await SavedRosterAsync(manager, name);
                var before = Stamps(loaded);
                Assert.Equal(5, before.Count);
                await ClockTickAsync();

                loaded.Name = name + "-renamed";
                await SaveAsync(manager, loaded, path);

                var after = Stamps(await LoadRosterAsync(manager, name + "-renamed"));
                Assert.True(after[loaded.ID] > before[loaded.ID], "The saved roster is written.");
                foreach (var (id, stamp) in before)
                    if (id != loaded.ID)
                        Assert.Equal(stamp, after[id]);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name || r.Name == name + "-renamed", Ct);
            }
        }

        [Fact]
        public async Task ChangedChild_IsTheOnlyChildWritten()
        {
            var manager = fixture.RequireManager();
            var name = Tag("cc");
            try
            {
                var loaded = await SavedRosterAsync(manager, name);
                var before = Stamps(loaded);
                await ClockTickAsync();

                var changed = loaded.Lines.Single(l => l.Code == $"{name}-a");
                changed.Hours = 4;
                await manager.SaveAsync(loaded, Ct);

                var reloaded = await LoadRosterAsync(manager, name);
                var after = Stamps(reloaded);
                Assert.Equal(4, reloaded.Lines.Single(l => l.ID == changed.ID).Hours);
                Assert.True(after[changed.ID] > before[changed.ID], "The changed line is written.");
                var untouched = reloaded.Lines.Single(l => l.ID != changed.ID);
                Assert.Equal(before[untouched.ID], after[untouched.ID]);
                foreach (var line in reloaded.Lines)
                    foreach (var pause in line.Breaks)
                        Assert.Equal(before[pause.ID], after[pause.ID]);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Theory]
        [InlineData(SavePath.ManagerSave)]
        [InlineData(SavePath.ContextBulkSaveAsync)]
        public async Task ChangedGrandchild_UnderUnchangedChild_IsWritten(SavePath path)
        {
            // The grandchild's write used to ride on its line's update, so skipping a clean line
            // must not drop it.
            var manager = fixture.RequireManager();
            var name = Tag("cg");
            try
            {
                var loaded = await SavedRosterAsync(manager, name);
                var before = Stamps(loaded);
                await ClockTickAsync();

                var line = loaded.Lines.Single(l => l.Code == $"{name}-a");
                var pause = line.Breaks.Single();
                pause.Minutes = 45;
                line.Breaks.Add(new IntRosterBreak { Minutes = 10 });
                await SaveAsync(manager, loaded, path);

                // In memory, the rows the save wrote take the client clock and the clean line keeps
                // its loaded stamp. The client and server clocks differ, so only equality is checked.
                Assert.NotEqual(before[loaded.ID], loaded.Timestamp);
                Assert.NotEqual(before[pause.ID], pause.Timestamp);
                Assert.Equal(before[line.ID], line.Timestamp);

                var reloaded = await LoadRosterAsync(manager, name);
                var after = Stamps(reloaded);
                var reloadedLine = reloaded.Lines.Single(l => l.ID == line.ID);
                Assert.Equal([10, 45], reloadedLine.Breaks.Select(b => b.Minutes).OrderBy(m => m));
                Assert.True(after[pause.ID] > before[pause.ID], "The changed break is written.");
                Assert.Equal(before[line.ID], after[line.ID]);
                Assert.True(after[loaded.ID] > before[loaded.ID], "The saved roster is written.");
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task UnchangedRoot_IsStillWrittenWhenSaved()
        {
            // An explicit save of an object writes it, so its Timestamp records the save.
            var manager = fixture.RequireManager();
            var name = Tag("ur");
            try
            {
                var loaded = await SavedRosterAsync(manager, name);
                var before = Stamps(loaded);
                await ClockTickAsync();

                Assert.NotNull(await manager.SaveAsync([loaded], Ct));

                var after = Stamps(await LoadRosterAsync(manager, name));
                Assert.True(after[loaded.ID] > before[loaded.ID], "The saved roster is written.");
                foreach (var (id, stamp) in before)
                    if (id != loaded.ID)
                        Assert.Equal(stamp, after[id]);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }
    }
}
