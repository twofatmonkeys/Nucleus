using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Child collections come back attached and unchanged on every load path. Before, the
    /// non-streaming paths read the parent rows and discarded the child result sets, and the
    /// streaming path attached children through the collection, which staged a change on each.
    /// </summary>
    public abstract class ChildCollectionLoadIntegrationTests(DatabaseFixture fixture)
    {
        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        private async Task<IntBundle> SaveBundleAsync(string name, int files)
        {
            var bundle = new IntBundle { Name = name };
            for (int i = 0; i < files; i++)
                bundle.Files.Add(new IntBundleFile { Name = $"{name}-{i}", Content = "content" });
            await fixture.Manager!.SaveAsync(bundle);
            return bundle;
        }

        private static void AssertAttachedAndClean(IntBundle bundle, int files)
        {
            Assert.Equal(files, bundle.Files.Count);
            Assert.All(bundle.Files, f =>
            {
                Assert.Same(bundle, f.Parent);
                Assert.False(f.HasChanges);
                Assert.False(f.IsNew);
            });
            Assert.False(bundle.HasChanges);
        }

        [Fact]
        public async Task LoadWhere_AttachesCleanChildren()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lw");
            await SaveBundleAsync(name, files: 3);

            var loaded = Assert.Single(manager.Get<IntBundle>().Where(b => b.Name == name).ToList());

            AssertAttachedAndClean(loaded, files: 3);
        }

        [Fact]
        public async Task LoadByID_AttachesCleanChildren()
        {
            var manager = fixture.RequireManager();
            var saved = await SaveBundleAsync(Tag("id"), files: 2);

            var loaded = manager.LoadByID<IntBundle>(saved.ID);

            Assert.NotNull(loaded);
            AssertAttachedAndClean(loaded!, files: 2);
        }

        [Fact]
        public async Task ToListAsync_AttachesCleanChildren()
        {
            var manager = fixture.RequireManager();
            var name = Tag("st");
            await SaveBundleAsync(name, files: 2);

            var loaded = Assert.Single(await manager.Get<IntBundle>().Where(b => b.Name == name).ToListAsync());

            AssertAttachedAndClean(loaded, files: 2);
        }

        [Fact]
        public async Task LoadPagedAsync_FetchesChildrenOfThePageOnly()
        {
            var manager = fixture.RequireManager();
            var prefix = Tag("pg");
            for (int i = 0; i < 3; i++)
                await SaveBundleAsync($"{prefix}-{i}", files: 2);
            Expression predicate = ((Expression<Func<IntBundle, bool>>)(b => b.Name!.StartsWith(prefix))).Body;

            var page = await manager.LoadPagedAsync<IntBundle>(1, 1, predicate);

            var bundle = Assert.Single(page);
            Assert.Equal(3, page.TotalCount);
            AssertAttachedAndClean(bundle, files: 2);

            // The child result set holds the page's children, not every matching parent's.
            var query = fixture.Provider!.ParseSelectExpression<IntBundle>(predicate);
            query.Skip = 1;
            query.Take = 1;
            using var context = fixture.Provider.CreateContext();
            var dataSet = context.GetDataSet(query);
            Assert.Equal(1, dataSet.RowCount);
            Assert.Equal(2, Assert.Single(dataSet.LinkedDataSets).RowCount);
        }

        [Fact]
        public async Task SavingALoadedParent_KeepsItsChildrenAttached()
        {
            // A save of the renamed parent leaves its unchanged children unwritten, and binding
            // them at load must not make them dirty, or the save would re-point their rows.
            var manager = fixture.RequireManager();
            var name = Tag("sv");
            await SaveBundleAsync(name, files: 2);
            var loaded = Assert.Single(manager.Get<IntBundle>().Where(b => b.Name == name).ToList());
            var contents = loaded.Files.ToDictionary(f => f.ID, f => f.Content);

            loaded.Name = name + "-renamed";
            await manager.SaveAsync(loaded);

            var reloaded = Assert.Single(manager.Get<IntBundle>().Where(b => b.Name == name + "-renamed").ToList());
            Assert.Equal(2, reloaded.Files.Count);
            Assert.All(reloaded.Files, f => Assert.Equal(contents[f.ID], f.Content));
        }
    }
}
