using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// <c>OnAfterLoadFromDB</c> runs once per loaded object on every load path, after the object
    /// is finished: its lines attached with their own hooks run and their parent bound, an
    /// excluded collection unloaded, and a lazy collection's rows finished when it loads. It used
    /// to run on no load path at all.
    /// </summary>
    public abstract class LoadHookIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static void AssertFinished(IntHookOwner owner, int lines = 2)
        {
            Assert.Equal(1, owner.HookRuns);
            Assert.True(owner.LinesWereLoaded);
            Assert.Equal(lines, owner.FinishedLinesSeen);
        }

        [Fact]
        public async Task EveryLoadPath_RunsTheHookOnce_AfterTheObjectIsFinished()
        {
            var manager = fixture.RequireManager();
            var name = $"hook-{Guid.NewGuid():N}";
            var owner = new IntHookOwner { Name = name };
            owner.Lines.Add(new IntHookLine { Sequence = 1 });
            owner.Lines.Add(new IntHookLine { Sequence = 2 });
            owner.Notes.Add(new IntHookNote { Sequence = 1 });
            await manager.SaveAsync(owner, Ct);
            Expression<Func<IntHookOwner, bool>> mine = o => o.Name == name;
            try
            {
                // Streamed (the queryable), sync, async, paged and by ID.
                AssertFinished(Assert.Single(await manager.Get<IntHookOwner>().Where(mine).ToListAsync(Ct)));
                AssertFinished(Assert.Single(manager.Load<IntHookOwner>(mine.Body)!));
                AssertFinished(Assert.Single((await manager.LoadAsync<IntHookOwner>(mine.Body, Ct))!));
                AssertFinished(Assert.Single(await manager.LoadPagedAsync<IntHookOwner>(0, 10, mine.Body, Ct)));
                AssertFinished(manager.LoadByID<IntHookOwner>(owner.ID)!);

                // Through a data context.
                using (var context = manager.DataProvider.CreateContext())
                    AssertFinished(Assert.Single(context.Load<IntHookOwner>(mine.Body)));

                // A lazy collection's rows run their hooks when it loads, with the owner bound.
                var lazy = Assert.Single(await manager.Get<IntHookOwner>().Where(mine).ToListAsync(Ct));
                var note = Assert.Single(lazy.Notes);
                Assert.Equal(1, note.HookRuns);
                Assert.True(note.SawParent);

                // Included, they run with the load.
                var included = Assert.Single(await manager.Get<IntHookOwner>().Include(o => o.Notes).Where(mine).ToListAsync(Ct));
                Assert.Equal(1, Assert.Single(included.Notes).HookRuns);

                // An excluded collection reads as unloaded inside the hook.
                var excluded = Assert.Single(await manager.Get<IntHookOwner>().Exclude(o => o.Lines).Where(mine).ToListAsync(Ct));
                Assert.Equal(1, excluded.HookRuns);
                Assert.False(excluded.LinesWereLoaded);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntHookOwner>(o => o.Name == name, Ct);
            }
        }
    }
}
