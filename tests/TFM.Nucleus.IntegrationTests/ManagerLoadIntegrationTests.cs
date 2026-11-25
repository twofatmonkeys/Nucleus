using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// The loads and the mixed-type save on <see cref="IObjectDataManager"/>, the interface DI
    /// registers: by ID, by predicate, paged and streamed, with <see cref="LoadOptions"/>.
    /// </summary>
    public abstract class ManagerLoadIntegrationTests(DatabaseFixture fixture) : IAsyncDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly List<string> _seeded = [];

        /// <summary>Deletes the widgets this test seeded; xunit disposes the class after each test.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var prefix in _seeded)
                await fixture.DeleteWidgetsAsync(prefix, CancellationToken.None);
        }

        private async Task<(IObjectDataManager Data, string Prefix, List<IntWidget> Widgets)> SeedAsync(int count)
        {
            IObjectDataManager data = fixture.RequireManager();
            var prefix = $"ml-{Guid.NewGuid():N}-";
            _seeded.Add(prefix);
            var widgets = Enumerable.Range(0, count).Select(i => new IntWidget { Name = $"{prefix}{i}", Quantity = i }).ToList();
            await data.SaveAsync(widgets, Ct);
            return (data, prefix, widgets);
        }

        [Fact]
        public async Task LoadByIDAsync_ReturnsTheRow_AndNullForAnUnknownID()
        {
            var (data, _, widgets) = await SeedAsync(1);

            var found = await data.LoadByIDAsync<IntWidget>(widgets[0].ID, Ct);
            var missing = await data.LoadByIDAsync<IntWidget>(Guid.NewGuid(), Ct);

            Assert.Equal(widgets[0].Name, found!.Name);
            Assert.Null(missing);
        }

        [Fact]
        public async Task LoadAsync_TakesAPredicate_AndReturnsAnEmptyCollectionForNoMatch()
        {
            var (data, prefix, _) = await SeedAsync(3);

            var matched = await data.LoadAsync<IntWidget>(w => w.Name!.StartsWith(prefix) && w.Quantity > 0, Ct);
            var none = await data.LoadAsync<IntWidget>(w => w.Name == prefix + "nobody", Ct);

            Assert.Equal(2, matched.Count());
            Assert.NotNull(none);
            Assert.Empty(none);
        }

        [Fact]
        public async Task AnUntypedLoad_AcceptsTheWholeLambda_NotOnlyItsBody()
        {
            // The untyped overloads took a predicate's Body; the lambda itself compiled and then
            // failed to translate.
            var (_, prefix, _) = await SeedAsync(2);
            var manager = fixture.RequireManager();
            Expression<Func<IntWidget, bool>> mine = w => w.Name!.StartsWith(prefix);

            var whole = await manager.LoadAsync<IntWidget>((Expression)mine, Ct);
            var body = await manager.LoadAsync<IntWidget>(mine.Body, Ct);

            Assert.Equal(2, whole!.Count());
            Assert.Equal(2, body!.Count());
        }

        [Fact]
        public async Task LoadPagedAsync_ReturnsThePageAndTheTotal()
        {
            var (data, prefix, _) = await SeedAsync(5);

            var page = await data.LoadPagedAsync<IntWidget>(1, 2, w => w.Name!.StartsWith(prefix), cancellationToken: Ct);

            Assert.Equal(2, page.Count);
            Assert.Equal(5, page.TotalCount);
        }

        [Fact]
        public async Task LoadStreamAsync_YieldsEveryMatch()
        {
            var (data, prefix, _) = await SeedAsync(4);
            var names = new List<string?>();

            await foreach (var widget in data.LoadStreamAsync<IntWidget>(w => w.Name!.StartsWith(prefix), cancellationToken: Ct))
                names.Add(widget.Name);

            Assert.Equal(4, names.Count);
        }

        [Fact]
        public async Task IncludeDeleted_SeesSoftDeletedRows_OnALoadAndOnAQuery()
        {
            IObjectDataManager data = fixture.RequireManager();
            var item = new IntSoftItem { Name = $"ml-sd-{Guid.NewGuid():N}" };
            var id = item.ID;
            await data.SaveAsync(item, Ct);
            await data.DeleteAsync(item, Ct);
            var deletedToo = new LoadOptions { IncludeDeleted = true };

            Assert.Null(await data.LoadByIDAsync<IntSoftItem>(id, Ct));
            Assert.NotNull(await data.LoadByIDAsync<IntSoftItem>(id, deletedToo, Ct));
            Assert.Empty(await data.LoadAsync<IntSoftItem>(x => x.ID == id, Ct));
            Assert.Single(await data.LoadAsync<IntSoftItem>(x => x.ID == id, deletedToo, Ct));
            Assert.Empty(await data.Get<IntSoftItem>().Where(x => x.ID == id).ToListAsync(Ct));
            Assert.Single(await data.Get<IntSoftItem>().IncludeDeleted().Where(x => x.ID == id).ToListAsync(Ct));
            Assert.Single(await data.Get<IntSoftItem>().Where(x => x.ID == id).IncludeDeleted().ToListAsync(Ct));

            // The operators that run in the database, and a row read, see them only when asked.
            Assert.Equal(0, await data.Get<IntSoftItem>().CountAsync(x => x.ID == id, Ct));
            Assert.Equal(1, await data.Get<IntSoftItem>().IncludeDeleted().CountAsync(x => x.ID == id, Ct));
            Assert.False(await data.Get<IntSoftItem>().AnyAsync(x => x.ID == id, Ct));
            Assert.True(await data.Get<IntSoftItem>().IncludeDeleted().AnyAsync(x => x.ID == id, Ct));
            Assert.Null(await data.Get<IntSoftItem>().FirstOrDefaultAsync(x => x.ID == id, Ct));
            Assert.NotNull(await data.Get<IntSoftItem>().IncludeDeleted().FirstOrDefaultAsync(x => x.ID == id, Ct));
            Assert.Equal(0, await data.Rows<IntSoftItem>(x => x.ID == id).CountAsync(Ct));
            Assert.Equal(1, await data.Rows<IntSoftItem>(x => x.ID == id).IncludeDeleted().CountAsync(Ct));
            Assert.Single(await data.Rows<IntSoftItem>(x => x.ID == id).IncludeDeleted().Select(x => x.Name).ToListAsync(Ct));
        }

        [Fact]
        public async Task FirstAndSingle_OnAQuery_ReadTheRowsTheyNeed()
        {
            var (data, prefix, widgets) = await SeedAsync(3);
            var mine = data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix));

            Assert.StartsWith(prefix, (await mine.FirstOrDefaultAsync(Ct))!.Name);
            Assert.StartsWith(prefix, (await mine.FirstAsync(Ct)).Name);
            Assert.Null(await data.Get<IntWidget>().FirstOrDefaultAsync(w => w.Name == prefix + "nobody", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.Get<IntWidget>().FirstAsync(w => w.Name == prefix + "nobody", Ct));

            Assert.Equal(widgets[1].ID, (await data.Get<IntWidget>().SingleAsync(w => w.Name == widgets[1].Name, Ct)).ID);
            Assert.Null(await data.Get<IntWidget>().SingleOrDefaultAsync(w => w.Name == prefix + "nobody", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mine.SingleAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mine.SingleOrDefaultAsync(Ct));
        }

        [Fact]
        public async Task AnyAndCount_OnAQuery_RunInTheDatabase()
        {
            var (data, prefix, _) = await SeedAsync(3);
            var mine = data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix));

            Assert.True(await mine.AnyAsync(Ct));
            Assert.True(await data.Get<IntWidget>().AnyAsync(w => w.Name!.StartsWith(prefix) && w.Quantity == 2, Ct));
            Assert.False(await data.Get<IntWidget>().AnyAsync(w => w.Name == prefix + "nobody", Ct));
            Assert.Equal(3, await mine.CountAsync(Ct));
            Assert.Equal(3L, await mine.LongCountAsync(Ct));
            Assert.Equal(2, await mine.CountAsync(w => w.Quantity > 0, Ct));
            Assert.Equal(0, await data.Get<IntWidget>().CountAsync(w => w.Name == prefix + "nobody", Ct));
        }

        [Fact]
        public async Task LoadFirst_ReadsOneRow_HoweverManyMatch()
        {
            var (_, prefix, _) = await SeedAsync(3);
            var manager = fixture.RequireManager();

            var first = await manager.LoadFirstAsync<IntWidget>(w => w.Name!.StartsWith(prefix), cancellationToken: Ct);
            var firstSync = manager.LoadFirst<IntWidget>(w => w.Name!.StartsWith(prefix));
            var none = await manager.LoadFirstAsync<IntWidget>(w => w.Name == prefix + "nobody", cancellationToken: Ct);

            Assert.StartsWith(prefix, first!.Name);
            Assert.StartsWith(prefix, firstSync!.Name);
            Assert.Null(none);
        }

        [Fact]
        public async Task TheRuntime_LoadsByIDAndEveryRow_OnTheAsynchronousPath()
        {
            var (_, _, widgets) = await SeedAsync(2);
            INucleusRuntime runtime = fixture.RequireManager();

            var found = await runtime.LoadByIDAsync<IntWidget>(widgets[0].ID, cancellationToken: Ct);
            var all = await runtime.LoadAllAsync<IntWidget>(Ct);

            Assert.Equal(widgets[0].Name, found!.Name);
            Assert.Contains(all, w => w.ID == widgets[1].ID);
        }

        [Fact]
        public async Task RollbackAsync_UndoesWhatTheContextWrote()
        {
            IObjectDataManager data = fixture.RequireManager();
            var widget = new IntWidget { Name = $"ml-rb-{Guid.NewGuid():N}", Quantity = 1 };
            _seeded.Add(widget.Name!);

            await using (var context = await data.CreateContextAsync<IntWidget>(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(widget, Ct);
                await context.RollbackAsync(Ct);
            }

            Assert.Null(await data.LoadByIDAsync<IntWidget>(widget.ID, Ct));
        }

        /// <summary>
        /// A context opened with no options had no transaction, and its CommitAsync did nothing,
        /// so a caller who forgot the option wrote each statement on its own and was not told.
        /// </summary>
        [Fact]
        public async Task CreateContextAsync_WithNoOptions_IsTransactional()
        {
            IObjectDataManager data = fixture.RequireManager();
            var widget = new IntWidget { Name = $"ml-tx-{Guid.NewGuid():N}", Quantity = 1 };
            _seeded.Add(widget.Name!);

            await using (var context = await data.CreateContextAsync<IntWidget>(token: Ct))
            {
                Assert.True(context.HasTransaction);
                await context.SaveAsync(widget, Ct);
                await context.RollbackAsync(Ct);
            }
            await using (var asked = await data.CreateContextAsync<IntWidget>(ContextOptions.Default, Ct))
                Assert.False(asked.HasTransaction);

            Assert.Null(await data.LoadByIDAsync<IntWidget>(widget.ID, Ct));
        }

        /// <summary>
        /// Work through a context the manager handed out left a [Cacheable] type's cache as it
        /// was, so a load after the commit answered with the rows from before it.
        /// </summary>
        [Fact]
        public async Task AContextFromTheManager_EvictsTheCacheOfWhatItWrote_OnceItCommits()
        {
            IObjectDataManager data = fixture.RequireManager();
            var kept = new IntCachedTag { Name = $"ml-cache-{Guid.NewGuid():N}" };
            var rolledBack = new IntCachedTag { Name = $"ml-cache-{Guid.NewGuid():N}" };
            Assert.DoesNotContain(await data.LoadAsync<IntCachedTag>(null, Ct), t => t.ID == kept.ID);

            await using (var context = await data.CreateContextAsync<IntCachedTag>(token: Ct))
            {
                await context.SaveAsync(rolledBack, Ct);
                await context.RollbackAsync(Ct);
            }
            Assert.DoesNotContain(await data.LoadAsync<IntCachedTag>(null, Ct), t => t.ID == rolledBack.ID);

            await using (var context = await data.CreateContextAsync<IntCachedTag>(token: Ct))
            {
                await context.SaveAsync(kept, Ct);
                await context.CommitAsync(Ct);
            }
            Assert.Contains(await data.LoadAsync<IntCachedTag>(null, Ct), t => t.ID == kept.ID);

            await using (var context = await data.CreateContextAsync<IntCachedTag>(token: Ct))
            {
                Assert.Equal(1, await context.DeleteWhereAsync<IntCachedTag>(t => t.ID == kept.ID, Ct));
                await context.CommitAsync(Ct);
            }
            Assert.DoesNotContain(await data.LoadAsync<IntCachedTag>(null, Ct), t => t.ID == kept.ID);
        }

        /// <summary>A save through a context the manager handed out was not audited; it is now, and only once it has committed.</summary>
        [Fact]
        public async Task AContextFromTheManager_AuditsWhatItSaved_OnlyOnceItCommits()
        {
            var shared = fixture.RequireManager();
            var log = new RecordingLogger();
            IObjectDataManager data = new ObjectDataManager(fixture.Provider!, shared.ObjectSchema, log);
            var kept = new IntWidget { Name = $"ml-audit-{Guid.NewGuid():N}", Quantity = 1 };
            var rolledBack = new IntWidget { Name = $"ml-audit-{Guid.NewGuid():N}", Quantity = 1 };
            _seeded.Add(kept.Name!);
            _seeded.Add(rolledBack.Name!);

            await using (var context = await data.CreateContextAsync<IntWidget>(token: Ct))
            {
                await context.SaveAsync(rolledBack, Ct);
                await context.RollbackAsync(Ct);
            }
            await using (var context = await data.CreateContextAsync<IntWidget>(token: Ct))
            {
                await context.SaveAsync(kept, Ct);
                Assert.DoesNotContain(log.Messages, m => m.Contains(kept.ID.ToString()));
                await context.CommitAsync(Ct);
            }

            Assert.Contains(log.Messages, m => m.StartsWith("Created") && m.Contains(kept.ID.ToString()));
            Assert.DoesNotContain(log.Messages, m => m.Contains(rolledBack.ID.ToString()));
        }

        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<ObjectDataManager>
        {
            public List<string> Messages { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
        }

        [Fact]
        public async Task SaveAsync_OfMixedTypes_WritesEachToItsOwnTable()
        {
            IObjectDataManager data = fixture.RequireManager();
            var tag = $"ml-mx-{Guid.NewGuid():N}";
            _seeded.Add(tag);
            var widget = new IntWidget { Name = tag, Quantity = 1 };
            var item = new IntHardItem { Name = tag };

            await data.SaveAsync(new ICoreObject?[] { widget, null, item }, Ct);

            Assert.False(widget.IsNew);
            Assert.False(item.IsNew);
            Assert.NotNull(await data.LoadByIDAsync<IntWidget>(widget.ID, Ct));
            Assert.NotNull(await data.LoadByIDAsync<IntHardItem>(item.ID, Ct));
        }
    }
}
