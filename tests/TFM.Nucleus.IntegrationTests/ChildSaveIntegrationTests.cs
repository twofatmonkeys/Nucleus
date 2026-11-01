namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A loaded parent saves its changed children on every save path. A child collection builds
    /// its writes through its parent's data provider, which only the manager's SaveAsync used to
    /// set, so every other path threw for a loaded parent.
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
            await using var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional, Ct);
            switch (path)
            {
                case SavePath.ContextSave: context.Save(bundle); break;
                case SavePath.ContextSaveAsync: await context.SaveAsync(bundle, Ct); break;
                case SavePath.ContextBulkSave: context.BulkSave([bundle]); break;
                case SavePath.ContextBulkSaveAsync: await context.BulkSaveAsync([bundle], Ct); break;
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
    }
}
