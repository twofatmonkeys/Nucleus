using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Delete by predicate and the child-row cascade, against the live database. Every row a
    /// test creates carries a unique tag, so tests never see each other's rows.
    /// </summary>
    [Collection("Database")]
    public class DeleteWhereIntegrationTests(DatabaseFixture fixture)
    {
        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        private List<IntBundleFile> FilesNamed(string name) =>
            fixture.Manager!.Get<IntBundleFile>().Where(f => f.Name == name).ToList();

        private async Task<IntBundle> SaveBundleAsync(string name, string fileName, int files)
        {
            var bundle = new IntBundle { Name = name };
            for (int i = 0; i < files; i++)
                bundle.Files.Add(new IntBundleFile { Name = fileName, Content = new string('x', 100) });
            await fixture.Manager!.SaveAsync(bundle);
            return bundle;
        }

        [Fact]
        public async Task DeleteWhereAsync_Hard_RemovesOnlyMatchingRowsAndCountsThem()
        {
            var manager = fixture.RequireManager();
            var keep = Tag("keep");
            var gone1 = Tag("gone");
            var gone2 = Tag("gone");
            foreach (var name in new[] { keep, gone1, gone2 })
                await manager.SaveAsync(new IntHardItem { Name = name });

            var affected = await manager.DeleteWhereAsync<IntHardItem>(x => x.Name == gone1 || x.Name == gone2);

            Assert.Equal(2, affected);
            Assert.Empty(manager.Get<IntHardItem>().Where(x => x.Name == gone1 || x.Name == gone2).ToList());
            Assert.Single(manager.Get<IntHardItem>().Where(x => x.Name == keep).ToList());
        }

        [Fact]
        public async Task DeleteWhereAsync_Soft_FlagsLiveRowsOnce()
        {
            var manager = fixture.RequireManager();
            var name = Tag("soft");
            var item = new IntSoftItem { Name = name };
            await manager.SaveAsync(item);

            var first = await manager.DeleteWhereAsync<IntSoftItem>(x => x.Name == name);
            var second = await manager.DeleteWhereAsync<IntSoftItem>(x => x.Name == name);

            Assert.Equal(1, first);
            Assert.Equal(0, second);
            Assert.Empty(manager.Get<IntSoftItem>().Where(x => x.Name == name).ToList());
            var query = fixture.Provider!.ParseSelectExpression<IntSoftItem>(
                ((Expression<Func<IntSoftItem, bool>>)(x => x.Name == name)).Body);
            query.IncludeDeleted = true;
            using var context = fixture.Provider.CreateContext();
            Assert.Equal(1, context.GetDataSet(query).RowCount);
        }

        [Fact]
        public async Task DeleteWhereAsync_Parent_RemovesItsChildRows()
        {
            var manager = fixture.RequireManager();
            var bundleName = Tag("bundle");
            var fileName = Tag("file");
            var otherFile = Tag("other");
            await SaveBundleAsync(bundleName, fileName, files: 3);
            await SaveBundleAsync(Tag("bundle"), otherFile, files: 1);

            var affected = await manager.DeleteWhereAsync<IntBundle>(b => b.Name == bundleName);

            Assert.Equal(1, affected);
            Assert.Empty(FilesNamed(fileName));
            Assert.Single(FilesNamed(otherFile));
        }

        [Fact]
        public async Task DeleteWhereAsync_SoftParent_RemovesHardChildRowsAndFlagsTheParent()
        {
            var manager = fixture.RequireManager();
            var name = Tag("softbundle");
            var noteName = Tag("note");
            var bundle = new IntSoftBundle { Name = name };
            bundle.Notes.Add(new IntSoftBundleNote { Name = noteName });
            await manager.SaveAsync(bundle);

            var affected = await manager.DeleteWhereAsync<IntSoftBundle>(b => b.Name == name);

            Assert.Equal(1, affected);
            Assert.Empty(manager.Get<IntSoftBundleNote>().Where(n => n.Name == noteName).ToList());
            Assert.Empty(manager.Get<IntSoftBundle>().Where(b => b.Name == name).ToList());
        }

        [Fact]
        public async Task EntityDelete_ParentWithoutItsChildrenInMemory_StillRemovesThem()
        {
            // Before the cascade moved into SQL, only children held in memory were deleted, so a
            // parent loaded without them left its child rows behind.
            var manager = fixture.RequireManager();
            var fileName = Tag("file");
            var saved = await SaveBundleAsync(Tag("bundle"), fileName, files: 2);
            var id = saved.ID;
            var loaded = Assert.Single(manager.Get<IntBundle>().Exclude(b => b.Files).Where(b => b.ID == id).ToList());
            Assert.False(loaded.Files.IsLoaded);

            await manager.DeleteAsync(loaded);

            Assert.Empty(manager.Get<IntBundle>().Where(b => b.ID == id).ToList());
            Assert.Empty(FilesNamed(fileName));
        }

        [Fact]
        public async Task ContextDeleteWhereAsync_RolledBack_PersistsNothing()
        {
            fixture.RequireSettings();
            var name = Tag("rollback");
            await fixture.Manager!.SaveAsync(new IntHardItem { Name = name });

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional))
            {
                var affected = await context.DeleteWhereAsync<IntHardItem>(x => x.Name == name);
                Assert.Equal(1, affected);
                await context.TryRollbackAsync();
            }

            Assert.Single(fixture.Manager.Get<IntHardItem>().Where(x => x.Name == name).ToList());
        }

        [Fact]
        public async Task DeleteWhereAsync_PredicateMatchingEveryRow_IsRefusedAndDeletesNothing()
        {
            var manager = fixture.RequireManager();
            var name = Tag("survivor");
            await manager.SaveAsync(new IntHardItem { Name = name });

            await Assert.ThrowsAsync<ArgumentException>(() => manager.DeleteWhereAsync<IntHardItem>(x => true));

            Assert.Single(manager.Get<IntHardItem>().Where(x => x.Name == name).ToList());
        }
    }
}
