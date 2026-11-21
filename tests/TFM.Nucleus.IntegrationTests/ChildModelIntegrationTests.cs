namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// The child model end to end: a removal saved with its owner deletes the row (first, in the
    /// same transaction, cascading to what it owns), a failed save keeps the removal for a retry,
    /// a soft owner deletes its undeclared children soft, the owner's save runs its children's
    /// rules, a forceSave link is written with the save that reaches it, a lazy collection
    /// stays out of a load until it is read or included, and a child type is paged, counted and
    /// deleted by its parent.
    /// </summary>
    public abstract class ChildModelIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        public enum SavePath
        {
            ManagerSave,
            ManagerSaveAsync,
            ContextSaveAsync,
            ContextBulkSaveAsync,
            ManagerBulkSave,
        }

        private async Task SaveAsync(ObjectDataManager manager, IntRoster roster, SavePath path)
        {
            switch (path)
            {
                case SavePath.ManagerSave:
                    manager.Save(roster);
                    return;
                case SavePath.ManagerSaveAsync:
                    await manager.SaveAsync(roster, Ct);
                    return;
                case SavePath.ManagerBulkSave:
                    manager.Save((IEnumerable<ICoreObject?>)[roster]);
                    return;
            }
            await using var context = await manager.Providers.AllProviders[0].CreateContextAsync(ContextOptions.Transactional, Ct);
            if (path == SavePath.ContextSaveAsync)
                await roster.SaveAsync(context, Ct);
            else
                await context.SaveAsync((IEnumerable<ICoreObject?>)[roster], Ct);
            await context.CommitAsync(Ct);
        }

        private static async Task<IntRoster> SavedRosterAsync(ObjectDataManager manager, string name, params string[] codes)
        {
            var roster = new IntRoster { Name = name };
            foreach (var code in codes)
            {
                var line = new IntRosterLine { Code = code, Hours = 8 };
                roster.Lines.Add(line);
                line.Breaks.Add(new IntRosterBreak { Minutes = 30 });
            }
            await manager.SaveAsync(roster, Ct);
            return Assert.Single(await manager.Get<IntRoster>().Where(r => r.Name == name).ToListAsync(Ct));
        }

        private static async Task<List<IntRosterLine>> LinesAsync(ObjectDataManager manager, IntRoster roster)
        {
            var reloaded = Assert.Single(await manager.Get<IntRoster>().Where(r => r.ID == roster.ID).ToListAsync(Ct));
            return [.. reloaded.Lines.OrderBy(l => l.Code)];
        }

        /// <summary>Rows matching the predicate, soft-deleted ones included.</summary>
        private int CountIncludingDeleted<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new()
        {
            var query = fixture.Provider!.ParseSelectExpression<T>(predicate.Body);
            query.IncludeDeleted = true;
            using var context = fixture.Provider.CreateContext();
            return context.GetDataSet(query).RowCount;
        }

        [Theory]
        [InlineData(SavePath.ManagerSave)]
        [InlineData(SavePath.ManagerSaveAsync)]
        [InlineData(SavePath.ContextSaveAsync)]
        [InlineData(SavePath.ContextBulkSaveAsync)]
        public async Task Removal_SavedWithTheOwner_DeletesTheRowAndWhatItOwns(SavePath path)
        {
            var manager = fixture.RequireManager();
            var name = Tag("rm");
            var roster = await SavedRosterAsync(manager, name, $"{name}-a", $"{name}-b");
            try
            {
                var removed = roster.Lines.Single(l => l.Code == $"{name}-a");
                Assert.Single(removed.Breaks);

                roster.Lines.Remove(removed);
                await SaveAsync(manager, roster, path);

                var lines = await LinesAsync(manager, roster);
                Assert.Equal([$"{name}-b"], lines.Select(l => l.Code));
                // The removed line's break went with it; the kept line's stayed.
                Assert.Equal(0, await manager.Rows<IntRosterBreak>(b => b.Parent.ID == removed.ID).CountAsync(Ct));
                Assert.Single(lines[0].Breaks);
                Assert.Empty(((TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection)roster.Lines).RemovedObjects);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task Replacement_WithTheSameUniqueCode_Saves_BecauseTheDeleteRunsFirst()
        {
            var manager = fixture.RequireManager();
            var name = Tag("rp");
            var roster = await SavedRosterAsync(manager, name, $"{name}-a");
            try
            {
                roster.Lines.Remove(roster.Lines.Single());
                roster.Lines.Add(new IntRosterLine { Code = $"{name}-a", Hours = 4 });

                await manager.SaveAsync(roster, Ct);

                var line = Assert.Single(await LinesAsync(manager, roster));
                Assert.Equal(4, line.Hours);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task FailedSave_RollsTheDeleteBack_AndKeepsTheRemovalForARetry()
        {
            var manager = fixture.RequireManager();
            var name = Tag("rb");
            var other = await SavedRosterAsync(manager, $"{name}-other", $"{name}-taken");
            var roster = await SavedRosterAsync(manager, name, $"{name}-a");
            try
            {
                var removed = roster.Lines.Single();
                roster.Lines.Remove(removed);
                var clash = new IntRosterLine { Code = $"{name}-taken", Hours = 1 };
                roster.Lines.Add(clash);

                await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(roster, Ct));

                // Rolled back: the removed row is still there, and still pending.
                Assert.Equal(1, await manager.Rows<IntRosterLine>(l => l.Code == $"{name}-a").CountAsync(Ct));
                Assert.Same(removed, Assert.Single(((TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection)roster.Lines).RemovedObjects));

                clash.Code = $"{name}-b";
                await manager.SaveAsync(roster, Ct);

                Assert.Equal(0, await manager.Rows<IntRosterLine>(l => l.Code == $"{name}-a").CountAsync(Ct));
                Assert.Equal([$"{name}-b"], (await LinesAsync(manager, roster)).Select(l => l.Code));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name || r.Name == $"{name}-other", Ct);
            }
        }

        [Fact]
        public async Task DeletingTheOwner_CascadesToUndeclaredChildrenAndTheirChildren()
        {
            var manager = fixture.RequireManager();
            var name = Tag("dc");
            var roster = await SavedRosterAsync(manager, name, $"{name}-a", $"{name}-b");
            var lineIds = roster.Lines.Select(l => l.ID).ToList();

            await manager.DeleteAsync(roster, Ct);

            Assert.Equal(0, await manager.Rows<IntRosterLine>(l => lineIds.Contains(l.ID)).CountAsync(Ct));
            Assert.Equal(0, await manager.Rows<IntRosterBreak>(b => lineIds.Contains(b.Parent.ID)).CountAsync(Ct));
        }

        [Fact]
        public async Task ALineDeletableOnlyThroughItsOwner_RefusesADirectDelete()
        {
            var manager = fixture.RequireManager();
            var name = Tag("dd");
            var roster = await SavedRosterAsync(manager, name, $"{name}-a");
            try
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => manager.DeleteAsync(roster.Lines.Single(), Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task SoftOwner_RemovesAndDeletesItsUndeclaredLinesSoft()
        {
            var manager = fixture.RequireManager();
            var name = Tag("so");
            var roster = new IntSoftRoster { Name = name };
            roster.Lines.Add(new IntSoftRosterLine { Code = $"{name}-a" });
            roster.Lines.Add(new IntSoftRosterLine { Code = $"{name}-b" });
            await manager.SaveAsync(roster, Ct);

            var loaded = Assert.Single(await manager.Get<IntSoftRoster>().Where(r => r.Name == name).ToListAsync(Ct));
            loaded.Lines.Remove(loaded.Lines.Single(l => l.Code == $"{name}-a"));
            await manager.SaveAsync(loaded, Ct);

            Assert.Equal(1, await manager.Rows<IntSoftRosterLine>(l => l.Code == $"{name}-a" || l.Code == $"{name}-b").CountAsync(Ct));
            Assert.Equal(2, CountIncludingDeleted<IntSoftRosterLine>(l => l.Code == $"{name}-a" || l.Code == $"{name}-b"));

            await manager.DeleteAsync(loaded, Ct);
            Assert.Equal(0, await manager.Rows<IntSoftRosterLine>(l => l.Code == $"{name}-b").CountAsync(Ct));
            Assert.Equal(1, CountIncludingDeleted<IntSoftRosterLine>(l => l.Code == $"{name}-b"));
        }

        [Theory]
        [InlineData(SavePath.ManagerSave)]
        [InlineData(SavePath.ManagerSaveAsync)]
        [InlineData(SavePath.ContextSaveAsync)]
        [InlineData(SavePath.ContextBulkSaveAsync)]
        [InlineData(SavePath.ManagerBulkSave)]
        public async Task EverySavePath_RunsTheChildrensRules_AndWritesNothing(SavePath path)
        {
            var manager = fixture.RequireManager();
            var roster = new IntRoster { Name = Tag("va") };
            roster.Lines.Add(new IntRosterLine { Code = Tag("va"), Hours = -1 });

            // Each path validates itself; none relies on the caller having called Validate().
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(manager, roster, path));
            Assert.Contains("negative hours", ex.Message);
            Assert.True(roster.IsNew);
            Assert.Equal(0, await manager.Rows<IntRoster>(r => r.Name == roster.Name).CountAsync(Ct));
        }

        [Fact]
        public async Task ForceSaveLink_IsWrittenWithTheSave_AndCommitted()
        {
            var manager = fixture.RequireManager();
            var name = Tag("fs");
            var target = new IntLinkTarget { Name = $"{name}-target" };
            await manager.SaveAsync(target, Ct);
            try
            {
                var source = new IntLinkSource { Name = name, Target = target };
                target.Name = $"{name}-renamed";

                await manager.SaveAsync(source, Ct);

                Assert.False(target.HasChanges);
                var stored = Assert.Single(await manager.Get<IntLinkTarget>().Where(t => t.ID == target.ID).ToListAsync(Ct));
                Assert.Equal($"{name}-renamed", stored.Name);

                // A new link is inserted with the save that reaches it.
                var fresh = new IntLinkTarget { Name = $"{name}-fresh" };
                var second = new IntLinkSource { Name = $"{name}-2", Target = fresh };
                await manager.SaveAsync(second, Ct);
                Assert.False(fresh.IsNew);
                Assert.Equal(1, await manager.Rows<IntLinkTarget>(t => t.ID == fresh.ID).CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task LazyCollection_StaysOutOfTheLoad_UntilItIsRead()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lz");
            var owner = new IntLazyOwner { Name = name };
            for (var i = 0; i < 3; i++)
                owner.Items.Add(new IntLazyItem { Sequence = i });
            await manager.SaveAsync(owner, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntLazyOwner>().Where(o => o.Name == name).ToListAsync(Ct));
                Assert.False(loaded.Items.IsLoaded);

                Assert.Equal([0, 1, 2], loaded.Items.Select(i => i.Sequence).OrderBy(s => s));
                Assert.True(loaded.Items.IsLoaded);
                Assert.All(loaded.Items, item => Assert.Same(loaded, (object)item.Parent));

                // Adding to an unread lazy collection saves with the owner.
                var other = Assert.Single(await manager.Get<IntLazyOwner>().Where(o => o.Name == name).ToListAsync(Ct));
                other.Items.Add(new IntLazyItem { Sequence = 3 });
                await manager.SaveAsync(other, Ct);
                Assert.False(other.Items.IsLoaded);
                Assert.Equal(4, await manager.Rows<IntLazyItem>(i => i.Parent == owner).CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLazyOwner>(o => o.Name == name, Ct);
            }
        }

        [Fact]
        public async Task Include_LoadsTheLazyCollectionOfEveryParent_InTheSameRead()
        {
            var manager = fixture.RequireManager();
            var name = Tag("in");
            var owners = Enumerable.Range(0, 3).Select(n =>
            {
                var owner = new IntLazyOwner { Name = $"{name}-{n}" };
                for (var i = 0; i <= n; i++)
                    owner.Items.Add(new IntLazyItem { Sequence = i });
                return owner;
            }).ToList();
            await manager.SaveAsync<IntLazyOwner>(owners, Ct);
            try
            {
                var loaded = await manager.Get<IntLazyOwner>().Include(o => o.Items).Where(o => o.Name!.StartsWith(name)).ToListAsync(Ct);

                Assert.Equal(3, loaded.Count);
                Assert.All(loaded, owner => Assert.True(owner.Items.IsLoaded));
                Assert.Equal([1, 2, 3], loaded.OrderBy(o => o.Name).Select(o => o.Items.Count));

                System.Linq.Expressions.Expression<Func<IntLazyOwner, bool>> mine = o => o.Name!.StartsWith(name);
                var page = await manager.LoadPagedAsync<IntLazyOwner>(0, 2, mine, new LoadOptions { Include = [nameof(IntLazyOwner.Items)] }, Ct);
                Assert.Equal(2, page.Count);
                Assert.All(page, owner => Assert.True(owner.Items.IsLoaded));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLazyOwner>(o => o.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task AChildType_IsPagedAndCountedByItsParent()
        {
            var manager = fixture.RequireManager();
            var name = Tag("pp");
            var owner = new IntLazyOwner { Name = name };
            var neighbour = new IntLazyOwner { Name = $"{name}-n" };
            for (var i = 0; i < 25; i++)
            {
                owner.Items.Add(new IntLazyItem { Sequence = i });
                neighbour.Items.Add(new IntLazyItem { Sequence = i });
            }
            await manager.SaveAsync<IntLazyOwner>([owner, neighbour], Ct);
            try
            {
                // A page of a large collection, read as the child type without loading its owner.
                System.Linq.Expressions.Expression<Func<IntLazyItem, bool>> mine = i => i.Parent.ID == owner.ID && i.Sequence >= 5;
                var page = await manager.LoadPagedAsync<IntLazyItem>(10, 5, mine.Body, Ct);
                Assert.Equal(5, page.Count);
                Assert.Equal(20, page.TotalCount);
                Assert.All(page, item => Assert.InRange(item.Sequence, 5, 24));

                Assert.Equal(25, await manager.Rows<IntLazyItem>(i => i.Parent == owner).CountAsync(Ct));
                var both = new List<IntLazyOwner> { owner, neighbour };
                Assert.Equal(50, await manager.Rows<IntLazyItem>(i => both.Contains(i.Parent)).CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLazyOwner>(o => o.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ADeletableChildType_IsDeletedByItsParent()
        {
            var manager = fixture.RequireManager();
            var name = Tag("dp");
            var bundles = Enumerable.Range(0, 2).Select(n =>
            {
                var bundle = new IntBundle { Name = $"{name}-{n}" };
                bundle.Files.Add(new IntBundleFile { Name = $"{name}-{n}-a" });
                bundle.Files.Add(new IntBundleFile { Name = $"{name}-{n}-b" });
                return bundle;
            }).ToList();
            await manager.SaveAsync<IntBundle>(bundles, Ct);
            try
            {
                Assert.Equal(2, await manager.DeleteWhereAsync<IntBundleFile>(f => f.Parent.ID == bundles[0].ID, Ct));

                Assert.Equal(0, await manager.Rows<IntBundleFile>(f => f.Parent == bundles[0]).CountAsync(Ct));
                Assert.Equal(2, await manager.Rows<IntBundleFile>(f => f.Parent == bundles[1]).CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntBundle>(b => b.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALinkedObject_FiltersByItsTarget()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lo");
            var target = new IntLinkTarget { Name = $"{name}-t" };
            var other = new IntLinkTarget { Name = $"{name}-o" };
            await manager.SaveAsync<IntLinkTarget>([target, other], Ct);
            try
            {
                await manager.SaveAsync<IntLinkSource>(
                [
                    new IntLinkSource { Name = $"{name}-1", Target = target },
                    new IntLinkSource { Name = $"{name}-2", Target = target },
                    new IntLinkSource { Name = $"{name}-3", Target = other },
                ], Ct);

                var linked = await manager.Get<IntLinkSource>().Where(s => s.Target == target).ToListAsync(Ct);
                Assert.Equal([$"{name}-1", $"{name}-2"], linked.Select(s => s.Name).Order());
                Assert.Equal(1, await manager.Rows<IntLinkSource>(s => s.Target!.ID == other.ID).CountAsync(Ct));
                Assert.Equal(1, await manager.DeleteWhereAsync<IntLinkSource>(s => s.Target!.ID == other.ID, Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALinkedObject_ReadsBackAsItsEntity_AfterAReload()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lr");
            var target = new IntLinkTarget { Name = $"{name}-t" };
            var source = new IntLinkSource { Name = name, Target = target };
            await manager.SaveAsync(source, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntLinkSource>().Where(s => s.ID == source.ID).ToListAsync(Ct));
                var linked = loaded.Target;
                Assert.NotNull(linked);
                Assert.Equal(target.ID, linked.ID);
                Assert.Equal($"{name}-t", linked.Name);
                Assert.Empty(((ICoreObject)loaded).ChangedProperties);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALinkedObjectWhoseRowIsGone_ReadsAsNull()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lg");
            var source = new IntLinkSource { Name = name, Target = new IntLinkTarget { Name = $"{name}-t" } };
            await manager.SaveAsync(source, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntLinkSource>().Where(s => s.ID == source.ID).ToListAsync(Ct));
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);

                Assert.Null(loaded.Target);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALoadedObject_WithAForceSaveLinkNobodyRead_SavesItsOwnChange()
        {
            var manager = fixture.RequireManager();
            var name = Tag("ls");
            var target = new IntLinkTarget { Name = $"{name}-t" };
            var source = new IntLinkSource { Name = name, Target = target };
            await manager.SaveAsync(source, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntLinkSource>().Where(s => s.ID == source.ID).ToListAsync(Ct));
                loaded.Name = $"{name}-renamed";
                await manager.SaveAsync(loaded, Ct);

                var reloaded = Assert.Single(await manager.Get<IntLinkSource>().Where(s => s.ID == source.ID).ToListAsync(Ct));
                Assert.Equal($"{name}-renamed", reloaded.Name);
                Assert.Equal(target.ID, reloaded.Target!.ID);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALinkedObjectReadAndChanged_IsWrittenByItsSourcesSave()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lw");
            var target = new IntLinkTarget { Name = $"{name}-t" };
            var source = new IntLinkSource { Name = name, Target = target };
            await manager.SaveAsync(source, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntLinkSource>().Where(s => s.ID == source.ID).ToListAsync(Ct));
                loaded.Target!.Name = $"{name}-t2";
                await manager.SaveAsync(loaded, Ct);

                var stored = Assert.Single(await manager.Get<IntLinkTarget>().Where(t => t.ID == target.ID).ToListAsync(Ct));
                Assert.Equal($"{name}-t2", stored.Name);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task ALinkedObjectOfAnObjectAContextLoaded_IsReadThroughThatProvider()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lc");
            var target = new IntLinkTarget { Name = $"{name}-t" };
            var source = new IntLinkSource { Name = name, Target = target };
            await manager.SaveAsync(source, Ct);
            try
            {
                System.Linq.Expressions.Expression<Func<IntLinkSource, bool>> mine = s => s.ID == source.ID;
                IntLinkSource loaded;
                using (var context = await manager.CreateContextAsync<IntLinkSource>(token: Ct))
                    loaded = Assert.Single(await context.LoadAsync<IntLinkSource>(mine.Body));

                // The context is gone; the read runs on a context of its own.
                Assert.Equal(target.ID, loaded.Target!.ID);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        /// <summary>
        /// Saves three sources over two targets and runs <paramref name="check"/> with the
        /// targets' rows deleted, so a reference reads back only if the load included it.
        /// </summary>
        private async Task WithIncludedTargetsAsync(Func<ObjectDataManager, string, Task> check)
        {
            var manager = fixture.RequireManager();
            var name = Tag("li");
            var first = new IntLinkTarget { Name = $"{name}-t1" };
            var second = new IntLinkTarget { Name = $"{name}-t2" };
            await manager.SaveAsync<IntLinkTarget>([first, second], Ct);
            try
            {
                await manager.SaveAsync<IntLinkSource>(
                [
                    new IntLinkSource { Name = $"{name}-1", Target = first },
                    new IntLinkSource { Name = $"{name}-2", Target = first },
                    new IntLinkSource { Name = $"{name}-3", Target = second },
                ], Ct);
                await check(manager, name);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLinkSource>(s => s.Name!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            }
        }

        /// <summary>Every source holds its target already: the rows are deleted, so nothing could load them now.</summary>
        private async Task AssertTargetsWereIncludedAsync(ObjectDataManager manager, string name, IReadOnlyList<IntLinkSource> sources)
        {
            await manager.DeleteWhereAsync<IntLinkTarget>(t => t.Name!.StartsWith(name), Ct);
            Assert.All(sources, s => Assert.NotNull(s.Target));
            Assert.All(sources, s => Assert.Empty(((ICoreObject)s).ChangedProperties));
        }

        [Fact]
        public async Task Include_LoadsTheLinkedObjectOfEveryRow_InTheSameRead()
        {
            await WithIncludedTargetsAsync(async (manager, name) =>
            {
                var loaded = (await manager.Get<IntLinkSource>().Include(s => s.Target).Where(s => s.Name!.StartsWith(name)).ToListAsync(Ct))
                    .OrderBy(s => s.Name).ToList();

                Assert.Equal(3, loaded.Count);
                await AssertTargetsWereIncludedAsync(manager, name, loaded);
                Assert.Same(loaded[0].Target, loaded[1].Target);
                Assert.Equal($"{name}-t2", loaded[2].Target!.Name);
            });
        }

        [Fact]
        public async Task Include_OnAPage_LoadsTheLinkedObjectsOfThatPage()
        {
            await WithIncludedTargetsAsync(async (manager, name) =>
            {
                System.Linq.Expressions.Expression<Func<IntLinkSource, bool>> mine = s => s.Name!.StartsWith(name);
                var page = await manager.LoadPagedAsync<IntLinkSource>(1, 2, mine, new LoadOptions { Include = [nameof(IntLinkSource.Target)] }, Ct);

                Assert.Equal(2, page.Count);
                Assert.Equal(3, page.TotalCount);
                await AssertTargetsWereIncludedAsync(manager, name, page);
            });
        }

        [Fact]
        public async Task Include_OnAStreamedRead_LoadsTheLinkedObjectOfEveryRow()
        {
            await WithIncludedTargetsAsync(async (manager, name) =>
            {
                System.Linq.Expressions.Expression<Func<IntLinkSource, bool>> mine = s => s.Name!.StartsWith(name);
                var streamed = new List<IntLinkSource>();
                await foreach (var source in manager.LoadStreamAsync<IntLinkSource>(mine, new LoadOptions { Include = [nameof(IntLinkSource.Target)] }, Ct))
                    streamed.Add(source);

                Assert.Equal(3, streamed.Count);
                await AssertTargetsWereIncludedAsync(manager, name, streamed);
            });
        }

        [Fact]
        public async Task Include_LoadsAReferencedEntityWithItsOwnChildren()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lk");
            var roster = new IntRoster { Name = name };
            roster.Lines.Add(new IntRosterLine { Code = $"{name}-a", Hours = 4 });
            roster.Lines.Add(new IntRosterLine { Code = $"{name}-b", Hours = 6 });
            await manager.SaveAsync(roster, Ct);
            var note = new IntRosterNote { Text = name, Roster = roster };
            await manager.SaveAsync(note, Ct);
            try
            {
                var loaded = Assert.Single(await manager.Get<IntRosterNote>().Include(n => n.Roster).Where(n => n.Text == name).ToListAsync(Ct));
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);

                var included = loaded.Roster;
                Assert.NotNull(included);
                Assert.True(included.Lines.IsLoaded);
                Assert.Equal([4, 6], included.Lines.Select(l => l.Hours).Order());
                Assert.All(included.Lines, line => Assert.Same(included, line.Parent));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntRosterNote>(n => n.Text == name, Ct);
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task Include_OfANameThatIsNeitherACollectionNorALinkedObject_Throws()
        {
            var manager = fixture.RequireManager();
            System.Linq.Expressions.Expression<Func<IntLinkSource, bool>> none = s => s.Name == "none";

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                manager.LoadAsync<IntLinkSource>(none, new LoadOptions { Include = [nameof(IntLinkSource.Name)] }, Ct));
            Assert.Contains("[LinkedObject]", ex.Message);
        }

        // ---- A [LinkedObject] to a child, and a child loaded on its own -----------------------

        /// <summary>
        /// Saves a roster with lines a and b, and a note linking to line a, runs
        /// <paramref name="check"/>, then deletes both.
        /// </summary>
        private async Task WithLineNoteAsync(Func<ObjectDataManager, string, IntRoster, IntLineNote, Task> check)
        {
            var manager = fixture.RequireManager();
            var name = Tag("ln");
            var roster = new IntRoster { Name = name };
            roster.Lines.Add(new IntRosterLine { Code = $"{name}-a", Hours = 4 });
            roster.Lines.Add(new IntRosterLine { Code = $"{name}-b", Hours = 6 });
            await manager.SaveAsync(roster, Ct);
            var note = new IntLineNote { Text = name, Line = roster.Lines.Single(l => l.Hours == 4) };
            await manager.SaveAsync(note, Ct);
            try
            {
                await check(manager, name, roster, note);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLineNote>(n => n.Text == name, Ct);
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);
            }
        }

        [Fact]
        public async Task ALinkToAChild_LoadsTheChildOnRead_AndTheChildsParentOnItsRead()
        {
            await WithLineNoteAsync(async (manager, name, roster, note) =>
            {
                var loaded = Assert.Single(await manager.Get<IntLineNote>().Where(n => n.ID == note.ID).ToListAsync(Ct));

                var line = loaded.Line;
                Assert.NotNull(line);
                Assert.Equal($"{name}-a", line.Code);
                Assert.Equal(roster.ID, line.Parent.ID);
                Assert.Equal(name, line.Parent.Name);
                Assert.Empty(((ICoreObject)loaded).ChangedProperties);
                Assert.Empty(((ICoreObject)line).ChangedProperties);
            });
        }

        [Fact]
        public async Task Include_LoadsALinkedChildInTheSameRead()
        {
            await WithLineNoteAsync(async (manager, name, roster, note) =>
            {
                var loaded = Assert.Single(await manager.Get<IntLineNote>().Include(n => n.Line).Where(n => n.ID == note.ID).ToListAsync(Ct));
                // The parent first, while its row is there; then the rows go, so the line reads only if the load held it.
                Assert.Equal(roster.ID, loaded.Line!.Parent.ID);
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct);

                Assert.Equal($"{name}-a", loaded.Line!.Code);
            });
        }

        [Fact]
        public async Task AChildLoadedOnItsOwn_ReadsItsParent()
        {
            await WithLineNoteAsync(async (manager, name, roster, note) =>
            {
                var queried = Assert.Single(await manager.Get<IntRosterLine>().Where(l => l.Code == $"{name}-b").ToListAsync(Ct));
                var byId = await manager.LoadByIDAsync<IntRosterLine>(note.Line!.ID, Ct);

                Assert.Equal(roster.ID, queried.Parent.ID);
                Assert.Equal(roster.ID, byId!.Parent.ID);
                Assert.Equal(2, byId.Parent.Lines.Count);
            });
        }

        [Fact]
        public async Task AChildAContextLoaded_ReadsItsParentThroughThatProvider()
        {
            await WithLineNoteAsync(async (manager, name, roster, note) =>
            {
                System.Linq.Expressions.Expression<Func<IntRosterLine, bool>> mine = l => l.Code == $"{name}-a";
                IntRosterLine loaded;
                using (var context = await manager.CreateContextAsync<IntRosterLine>(token: Ct))
                    loaded = Assert.Single(await context.LoadAsync<IntRosterLine>(mine.Body));

                // The context is gone; the read runs on a context of its own.
                Assert.Equal(roster.ID, loaded.Parent.ID);
            });
        }

        [Fact]
        public async Task ALinkToAChildRemovedFromItsCollection_ReadsAsNull()
        {
            await WithLineNoteAsync(async (manager, name, roster, note) =>
            {
                roster.Lines.Remove(roster.Lines.Single(l => l.Hours == 4));
                await manager.SaveAsync(roster, Ct);

                var loaded = Assert.Single(await manager.Get<IntLineNote>().Where(n => n.ID == note.ID).ToListAsync(Ct));
                Assert.Null(loaded.Line);
            });
        }
    }
}
