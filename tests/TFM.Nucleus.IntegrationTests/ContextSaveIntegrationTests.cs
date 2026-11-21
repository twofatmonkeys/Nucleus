using TFM.Nucleus.Abstractions.Interfaces;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// An object saved through a caller-owned context follows the context's transaction in memory:
    /// marked saved as the writes run, kept saved when the transaction commits, and put back as it
    /// was before its first save in the transaction when it rolls back, so a retry stores it without
    /// a reload. Before, CoreObject.SaveAsync(context) marked the object saved for good before the
    /// commit, and IDataContext.Save never marked it saved at all, so a new object was inserted
    /// again by its next save.
    /// </summary>
    public abstract class ContextSaveIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        public enum SavePath
        {
            CoreObjectSaveAsync,
            ContextSave,
            ContextSaveAsync,
            ContextBatchSave,
            ContextBatchSaveAsync,
        }

        public static TheoryData<SavePath> Paths => new(Enum.GetValues<SavePath>());

        private static async Task SaveThroughAsync(IDataContext context, CoreObject obj, SavePath path)
        {
            switch (path)
            {
                case SavePath.CoreObjectSaveAsync: await obj.SaveAsync(context, Ct); break;
                case SavePath.ContextSave: context.Save(obj); break;
                case SavePath.ContextSaveAsync: await context.SaveAsync(obj, Ct); break;
                case SavePath.ContextBatchSave: context.Save((IEnumerable<ICoreObject?>)[obj]); break;
                case SavePath.ContextBatchSaveAsync: await context.SaveAsync((IEnumerable<ICoreObject?>)[obj], Ct); break;
            }
        }

        private static IntRoster NewRoster(string name, params string[] codes)
        {
            var roster = new IntRoster { Name = name };
            foreach (var code in codes)
                roster.Lines.Add(new IntRosterLine { Code = code, Hours = 8 });
            return roster;
        }

        private static async Task<List<IntRoster>> RostersAsync(ObjectDataManager manager, string name) =>
            await manager.Get<IntRoster>().Where(r => r.Name!.StartsWith(name)).ToListAsync(Ct);

        private static Task CleanUpAsync(ObjectDataManager manager, string name) =>
            manager.DeleteWhereAsync<IntRoster>(r => r.Name!.StartsWith(name), Ct);

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task Commit_KeepsTheGraphSaved_SoTheNextSaveUpdates(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("ctx-commit");
            var roster = NewRoster(name, $"{name}-a");
            try
            {
                await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                {
                    await SaveThroughAsync(context, roster, path);
                    await context.CommitAsync(Ct);
                }

                Assert.False(roster.IsNew);
                Assert.False(roster.HasChanges);
                Assert.False(Assert.Single(roster.Lines).IsNew);

                roster.Name = $"{name}-renamed";
                await manager.SaveAsync(roster, Ct);

                var stored = Assert.Single(await RostersAsync(manager, name));
                Assert.Equal($"{name}-renamed", stored.Name);
                Assert.Single(stored.Lines);
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task Rollback_PutsANewGraphBack_SoASaveAfterwardsStoresIt(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("ctx-rollback-new");
            var roster = NewRoster(name, $"{name}-a");
            try
            {
                await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                {
                    await SaveThroughAsync(context, roster, path);
                    Assert.False(roster.IsNew);
                    await context.RollbackAsync(Ct);
                }

                Assert.True(roster.IsNew);
                Assert.True(Assert.Single(roster.Lines).IsNew);
                Assert.Empty(await RostersAsync(manager, name));

                await manager.SaveAsync(roster, Ct);

                var stored = Assert.Single(await RostersAsync(manager, name));
                Assert.Equal($"{name}-a", Assert.Single(stored.Lines).Code);
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task Rollback_PutsALoadedGraphBack_WithItsChangesAndRemovalsPending(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("ctx-rollback-loaded");
            await manager.SaveAsync(NewRoster(name, $"{name}-a", $"{name}-b"), Ct);
            try
            {
                var roster = Assert.Single(await RostersAsync(manager, name));
                var removed = roster.Lines.Single(l => l.Code == $"{name}-a");
                var kept = roster.Lines.Single(l => l.Code == $"{name}-b");
                roster.Name = $"{name}-renamed";
                roster.Lines.Remove(removed);
                kept.Hours = 4;
                var added = new IntRosterLine { Code = $"{name}-c", Hours = 2 };
                roster.Lines.Add(added);

                await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                {
                    await SaveThroughAsync(context, roster, path);
                    await context.RollbackAsync(Ct);
                }

                Assert.True(roster.HasChanges);
                Assert.True(kept.HasChanges);
                Assert.True(added.IsNew);
                Assert.Same(removed, Assert.Single(((INucleusChildCollection)roster.Lines).RemovedObjects));
                var untouched = Assert.Single(await RostersAsync(manager, name));
                Assert.Equal(name, untouched.Name);
                Assert.Equal([$"{name}-a", $"{name}-b"], untouched.Lines.Select(l => l.Code).Order());

                await manager.SaveAsync(roster, Ct);

                var stored = Assert.Single(await RostersAsync(manager, name));
                Assert.Equal($"{name}-renamed", stored.Name);
                Assert.Equal([($"{name}-b", 4), ($"{name}-c", 2)], stored.Lines.Select(l => (l.Code, l.Hours)).Order());
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task SavingANewGraphTwiceInOneTransaction_WritesItOnce(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("ctx-twice");
            var roster = NewRoster(name, $"{name}-a");
            try
            {
                await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                {
                    await SaveThroughAsync(context, roster, path);
                    roster.Name = $"{name}-second";
                    await SaveThroughAsync(context, roster, path);
                    await context.CommitAsync(Ct);
                }

                var stored = Assert.Single(await RostersAsync(manager, name));
                Assert.Equal($"{name}-second", stored.Name);
                Assert.Single(stored.Lines);
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task AFailedSave_DoomsTheTransaction_AndPutsBackWhatWasSavedBeforeIt()
        {
            var manager = fixture.RequireManager();
            var code = Tag("ctx-unique");
            await manager.SaveAsync(new IntUniqueItem { Code = code }, Ct);
            var widget = new IntWidget { Name = Tag("ctx-before-failure"), Quantity = 1 };

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(widget, Ct);
                await Assert.ThrowsAsync<UniqueViolationException>(() => context.SaveAsync(new IntUniqueItem { Code = code }, Ct));
            }

            Assert.True(widget.IsNew);
            Assert.Null(await manager.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct));
        }

        [Fact]
        public async Task ACommitOfADoomedTransaction_PutsTheObjectsBack()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = Tag("ctx-doomed"), Quantity = 1 };

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(widget, Ct);
                context.Rollback();
                var ex = await Assert.ThrowsAsync<DataContextException>(() => context.CommitAsync(Ct));
                Assert.Equal(DataContextErrorKind.TransactionDoomed, ex.Kind);
            }

            Assert.True(widget.IsNew);
            Assert.Null(await manager.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct));
        }

        [Fact]
        public async Task ADisposeThatCommits_KeepsTheObjectsSaved()
        {
            // The shared stack leaves RequireCommit off, so a dispose without a commit commits.
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = Tag("ctx-implicit"), Quantity = 1 };

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                await context.SaveAsync(widget, Ct);

            Assert.False(widget.IsNew);
            Assert.NotNull(await manager.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct));
        }

        [Fact]
        public async Task AContextWithoutATransaction_MarksTheObjectSavedAtTheWrite()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = Tag("ctx-no-transaction"), Quantity = 1 };

            await using (var context = await fixture.Provider!.CreateContextAsync(token: Ct))
            {
                await context.SaveAsync(widget, Ct);
                Assert.False(widget.IsNew);
            }

            widget.Quantity = 2;
            await manager.SaveAsync(widget, Ct);
            var stored = await manager.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct);
            Assert.Equal(2, stored!.Quantity);
        }

        [Fact]
        public async Task AChildContextsSaves_ArePutBack_WhenTheRootRollsBack()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = Tag("ctx-child"), Quantity = 1 };

            await using (var root = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await using (var child = await fixture.Provider.CreateContextAsync(new ContextOptions { UseTransaction = true, Parent = root }, Ct))
                {
                    await child.SaveAsync(widget, Ct);
                    await child.CommitAsync(Ct);
                }
                Assert.False(widget.IsNew);
                await root.RollbackAsync(Ct);
            }

            Assert.True(widget.IsNew);
            Assert.Null(await manager.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct));
        }
    }
}
