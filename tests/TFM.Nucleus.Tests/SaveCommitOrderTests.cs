namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A manager save marks its objects saved only once its transaction has committed. Before,
    /// the objects were marked saved and the cache evicted inside the context's scope, so a commit
    /// that failed left them marked saved with nothing in the database. The fake's CommitFailure
    /// is that failed commit.
    /// </summary>
    public class SaveCommitOrderTests
    {
        public static TheoryData<string> Saves => new() { "Save", "SaveAsync", "BulkSave" };

        private static (ObjectDataManager Manager, FakeDataProvider Provider) NewManager()
        {
            var provider = new FakeDataProvider();
            return (new ObjectDataManager(provider, new ObjectSchemaProvider(), null), provider);
        }

        private static async Task SaveThrough<T>(ObjectDataManager manager, string path, T obj) where T : ICoreObject<T>, new()
        {
            switch (path)
            {
                case "Save": manager.Save(obj); break;
                case "SaveAsync": await manager.SaveAsync(obj, CancellationToken.None); break;
                default: manager.Save((IEnumerable<ICoreObject?>)[obj]); break;
            }
        }

        [Theory]
        [MemberData(nameof(Saves))]
        public async Task AFailedCommit_LeavesTheObjectUnsaved_WithItsChanges(string path)
        {
            var (manager, provider) = NewManager();
            var commitFailure = new InvalidOperationException("commit failed");
            provider.CommitFailure = commitFailure;
            var widget = new TestWidget { Name = "w" };

            var thrown = await Record.ExceptionAsync(() => SaveThrough(manager, path, widget));

            Assert.NotNull(thrown);
            Assert.True(thrown == commitFailure || thrown.InnerException == commitFailure,
                "The save must fail with the commit's failure, wrapped or not.");
            Assert.True(widget.IsNew);
            Assert.Equal("w", Assert.Contains(nameof(TestWidget.Name), widget.ChangedProperties));
        }

        [Theory]
        [MemberData(nameof(Saves))]
        public async Task AFailedCommit_LeavesTheCacheAsItWas(string path)
        {
            var (manager, provider) = NewManager();
            provider.SetupRows<CachedWidget>(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "cached" });
            manager.Load<CachedWidget>();
            provider.CommitFailure = new InvalidOperationException("commit failed");

            await Record.ExceptionAsync(() => SaveThrough(manager, path, new CachedWidget { Name = "new" }));

            Assert.True(manager.Cache.IsCached(typeof(CachedWidget)));
        }

        [Theory]
        [MemberData(nameof(Saves))]
        public async Task ACommittedSave_MarksTheObjectSaved_AndEvictsTheCache(string path)
        {
            var (manager, provider) = NewManager();
            provider.SetupRows<CachedWidget>(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "cached" });
            manager.Load<CachedWidget>();
            var widget = new CachedWidget { Name = "new" };

            await SaveThrough(manager, path, widget);

            Assert.False(widget.IsNew);
            Assert.False(manager.Cache.IsCached(typeof(CachedWidget)));
        }

        [Fact]
        public async Task SaveAsync_CommitsOnTheAsynchronousPath()
        {
            var (manager, provider) = NewManager();

            await manager.SaveAsync(new TestWidget { Name = "w" }, CancellationToken.None);

            var context = Assert.Single(provider.Contexts);
            Assert.True(context.CommittedAsync);
            Assert.True(context.DisposedAsync);
        }

        [Theory]
        [MemberData(nameof(Saves))]
        public async Task ASave_CommitsItsContextExplicitly(string path)
        {
            var (manager, provider) = NewManager();

            await SaveThrough(manager, path, new TestWidget { Name = "w" });

            Assert.Equal(path == "SaveAsync", Assert.Single(provider.Contexts).CommittedAsync);
        }
    }
}
