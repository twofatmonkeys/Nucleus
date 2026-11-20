using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Loading a parent without a child collection: the child rows are never queried, the
    /// collection says it is unloaded, and it can be loaded later by the parent's ID.
    /// </summary>
    public abstract class ExcludeIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        private async Task<IntBundle> SaveBundleAsync(string name, int files)
        {
            var bundle = new IntBundle { Name = name };
            for (int i = 0; i < files; i++)
                bundle.Files.Add(new IntBundleFile { Name = $"{name}-{i}", Content = "content" });
            await fixture.Manager!.SaveAsync(bundle);
            return bundle;
        }

        [Fact]
        public async Task Exclude_NeverQueriesTheChildRows()
        {
            fixture.RequireSettings();
            var name = Tag("nq");
            await SaveBundleAsync(name, files: 3);

            var query = fixture.Provider!.ParseSelectExpression<IntBundle>(
                ((Expression<Func<IntBundle, bool>>)(b => b.Name == name)).Body);
            Assert.True(query.ExcludeChildCollection(nameof(IntBundle.Files)));
            using var context = fixture.Provider.CreateContext();
            var dataSet = context.GetDataSet(query);

            Assert.Equal(1, dataSet.RowCount);
            Assert.Empty(dataSet.LinkedDataSets);
        }

        /// <summary>
        /// The same read as a store makes it: through IObjectDataManager, the type DI registers,
        /// with a token on every call, paging after Where, finding by ID and loading the
        /// excluded collection without a cast.
        /// </summary>
        [Fact]
        public async Task ThroughTheInterface_PageFindAndLoadAnExcludedCollection()
        {
            IObjectDataManager data = fixture.RequireManager();
            var name = Tag("if");
            await SaveBundleAsync(name, files: 2);

            var page = await data.Get<IntBundle>().Where(b => b.Name == name).Exclude(b => b.Files).ToPagedResultAsync(0, 10, Ct);
            var listed = Assert.Single(page);
            Assert.Equal(1, page.TotalCount);
            Assert.False(listed.Files.IsLoaded);

            await data.LoadChildCollectionAsync(listed.Files, Ct);
            Assert.Equal(2, listed.Files.Count);

            var found = await data.Get<IntBundle>().FindAsync(listed.ID, Ct);
            Assert.Equal(2, found!.Files.Count);
        }

        [Fact]
        public async Task Exclude_ThenLoadChildCollection_FillsItClean()
        {
            var manager = fixture.RequireManager();
            var name = Tag("ld");
            await SaveBundleAsync(name, files: 3);

            var bundle = Assert.Single(await manager.Get<IntBundle>().Where(b => b.Name == name).Exclude(b => b.Files).ToListAsync());
            Assert.False(bundle.Files.IsLoaded);
            Assert.Throws<InvalidOperationException>(() => bundle.Files.Count);

            await manager.LoadChildCollectionAsync(bundle.Files);

            Assert.True(bundle.Files.IsLoaded);
            Assert.Equal(3, bundle.Files.Count);
            Assert.All(bundle.Files, f =>
            {
                Assert.Same(bundle, f.Parent);
                Assert.False(f.HasChanges);
            });
            Assert.False(bundle.HasChanges);
        }

        [Fact]
        public async Task Exclude_OnEveryLoadPath()
        {
            var manager = fixture.RequireManager();
            var name = Tag("ep");
            var saved = await SaveBundleAsync(name, files: 2);
            Expression predicate = ((Expression<Func<IntBundle, bool>>)(b => b.Name == name)).Body;
            string[] files = [nameof(IntBundle.Files)];

            var bySyncLoad = Assert.Single(manager.Load<IntBundle>(predicate, files)!);
            var byAsyncLoad = Assert.Single((await manager.LoadAsync<IntBundle>(predicate, files, CancellationToken.None))!);
            var byId = manager.LoadByID<IntBundle>(saved.ID, files);
            var byPage = Assert.Single(await manager.LoadPagedAsync<IntBundle>(0, 10, predicate, files));

            Assert.All(new[] { bySyncLoad, byAsyncLoad, byId!, byPage }, b => Assert.False(b.Files.IsLoaded));
        }

        [Fact]
        public async Task AddingToAnUnloadedCollection_SavesTheNewChild_AndLoadingDoesNotDuplicateIt()
        {
            var manager = fixture.RequireManager();
            var name = Tag("add");
            await SaveBundleAsync(name, files: 2);
            var bundle = Assert.Single(manager.Get<IntBundle>().Where(b => b.Name == name).Exclude(b => b.Files).ToList());

            bundle.Files.Add(new IntBundleFile { Name = $"{name}-new", Content = "added" });
            await manager.SaveAsync(bundle);
            await manager.LoadChildCollectionAsync(bundle.Files);

            Assert.Equal(3, bundle.Files.Count);
            var reloaded = Assert.Single(manager.Get<IntBundle>().Where(b => b.Name == name).ToList());
            Assert.Equal(3, reloaded.Files.Count);
            Assert.Single(reloaded.Files, f => f.Content == "added");
        }
    }
}
