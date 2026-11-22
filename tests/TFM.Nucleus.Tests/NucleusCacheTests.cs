using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Tests
{
    [Cacheable(refreshInterval: 0, cacheLifetime: 300)]
    public class CachedWidget : CoreObject<CachedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Cacheable(refreshInterval: 60, cacheLifetime: 0)]
    public class RefreshingWidget : CoreObject<RefreshingWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class NucleusCacheTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IObjectSchema Schema<T>() where T : ICoreObject<T>, new() => new ObjectSchemaProvider().Get<T>()!;

        private static NucleusCollection<T> Collection<T>() where T : ICoreObject<T>, new() => [new T()];

        /// <summary>
        /// True when <paramref name="actual"/> is a list of its own holding the one object
        /// <paramref name="loaded"/> held: the cache hands out copies, never the loader's list.
        /// </summary>
        private static bool Holds<T>(NucleusCollection<T> loaded, NucleusCollection<T> actual) where T : ICoreObject<T>, new() =>
            !ReferenceEquals(loaded, actual) && actual.Count == 1 && ReferenceEquals(loaded[0], actual[0]);

        private sealed class TestClock
        {
            public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        }

        private static (ObjectDataManager Manager, FakeDataProvider Provider, TestClock Clock) NewManager()
        {
            var clock = new TestClock();
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(
                new DataProviderRegistry([provider]), new ObjectSchemaProvider(),
                cache: new NucleusCache(() => clock.Now));
            return (manager, provider, clock);
        }

        private static Dictionary<string, object?> Row(string name) => new()
        {
            ["ID"] = Guid.NewGuid(),
            ["Name"] = name,
        };

        [Fact]
        public void LoadAll_CacheableType_HitsProviderOnce()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            var first = manager.Load<CachedWidget>();
            var second = manager.Load<CachedWidget>();

            Assert.NotSame(first, second); // each caller gets a list of its own
            Assert.Same(Assert.Single(first), Assert.Single(second)); // over the shared cached objects
            Assert.Single(provider.Contexts); // only one provider round trip
        }

        [Fact]
        public void LoadAll_CacheableType_ChangingTheListLeavesTheCacheAlone()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            var mine = manager.Load<CachedWidget>();
            mine.Add(new CachedWidget { Name = "added by one caller" });
            mine.RemoveAt(0);

            Assert.Equal("cached", Assert.Single(manager.Load<CachedWidget>()).Name);
            Assert.Single(provider.Contexts);
        }

        [Fact]
        public async Task LoadAsync_EveryRowOfACacheableType_AnswersFromTheCache()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            var first = await manager.LoadAsync<CachedWidget>(cancellationToken: Ct);
            var second = await manager.LoadAsync<CachedWidget>(x => true, Ct);
            var third = manager.Load<CachedWidget>();
            var all = await ((INucleusRuntime)manager).LoadAllAsync<CachedWidget>(Ct);

            Assert.Single(provider.Contexts);
            Assert.Single(provider.ContextTokens); // the one fill ran on the asynchronous path
            Assert.NotSame(first, second);
            Assert.Same(Assert.Single(first), Assert.Single(second));
            Assert.Same(Assert.Single(first), Assert.Single(third));
            Assert.Same(Assert.Single(first), Assert.Single(all));
        }

        [Fact]
        public async Task LoadAsync_OfACacheableType_ThatTheCacheDoesNotHold_ReadsTheTable()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            await manager.LoadAsync<CachedWidget>(x => x.Name == "cached", Ct); // filtered
            await manager.LoadAsync<CachedWidget>(null, new LoadOptions { IncludeDeleted = true }, Ct); // not only live rows

            Assert.Equal(2, provider.Contexts.Count);
            Assert.False(manager.Cache.IsCached(typeof(CachedWidget)));
        }

        [Fact]
        public async Task LoadByIDAsync_ColdCache_FillsOnTheAsynchronousPath()
        {
            var (manager, provider, _) = NewManager();
            var id = Guid.NewGuid();
            provider.SetupRows<CachedWidget>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "indexed" });

            var found = await manager.LoadByIDAsync<CachedWidget>(id, Ct);
            var again = await manager.LoadByIDAsync<CachedWidget>(id, Ct);

            Assert.Equal("indexed", found!.Name);
            Assert.Same(found, again);
            Assert.Single(provider.Contexts);
            Assert.Single(provider.ContextTokens);
        }

        [Fact]
        public async Task GetOrLoadByIdAsync_AnEvictionDuringTheFill_StillFindsTheRow()
        {
            // The by-ID read filled the cache and then looked the ID up as a second step, so a
            // save's eviction in between made a stored row read as missing.
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();
            var row = new CachedWidget { Name = "stored" };
            var gate = new TaskCompletionSource<NucleusCollection<CachedWidget>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var reading = cache.GetOrLoadByIdAsync<CachedWidget>(schema, row.ID, _ => gate.Task, Ct);
            cache.Evict(typeof(CachedWidget));
            gate.SetResult([row]);

            Assert.Same(row, await reading);
            Assert.False(cache.IsCached(typeof(CachedWidget)));
        }

        [Fact]
        public void GetOrLoadById_FindsTheRow_AndTheDefaultForAnUnknownID()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();
            var row = new CachedWidget { Name = "stored" };

            Assert.Same(row, cache.GetOrLoadById<CachedWidget>(schema, row.ID, () => [row]));
            Assert.Null(cache.GetOrLoadById<CachedWidget>(schema, Guid.NewGuid(), () => throw new InvalidOperationException("a second load")));
        }

        [Fact]
        public async Task AQueryWithNoWhere_OfACacheableType_StreamsFromTheCache()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            var listed = await manager.Get<CachedWidget>().ToListAsync(Ct);
            var enumerated = new List<CachedWidget>();
            await foreach (var widget in manager.Get<CachedWidget>().WithCancellation(Ct))
                enumerated.Add(widget);
            var streamed = new List<CachedWidget>();
            await foreach (var widget in manager.LoadStreamAsync<CachedWidget>(cancellationToken: Ct))
                streamed.Add(widget);
            var loaded = manager.Load<CachedWidget>();

            Assert.Single(provider.Contexts); // one fill, on the asynchronous path
            Assert.Single(provider.ContextTokens);
            Assert.Same(Assert.Single(listed), Assert.Single(enumerated));
            Assert.Same(listed[0], Assert.Single(streamed));
            Assert.Same(listed[0], Assert.Single(loaded));
        }

        [Fact]
        public async Task AQueryWithAWhere_OrThatSeesDeletedRows_StillStreamsFromTheTable()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("cached"));

            await manager.Get<CachedWidget>().Where(w => w.Name == "cached").ToListAsync(Ct);
            await manager.Get<CachedWidget>().IncludeDeleted().ToListAsync(Ct);

            Assert.Equal(2, provider.Contexts.Count);
            Assert.False(manager.Cache.IsCached(typeof(CachedWidget)));
        }

        [Fact]
        public async Task GetOrLoadAsync_ConcurrentMisses_ShareOneLoad()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();
            var gate = new TaskCompletionSource<NucleusCollection<CachedWidget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = 0;
            Task<NucleusCollection<CachedWidget>> Load(CancellationToken _)
            {
                Interlocked.Increment(ref loads);
                return gate.Task;
            }

            var first = cache.GetOrLoadAsync<CachedWidget>(schema, Load, false, Ct);
            var second = cache.GetOrLoadAsync<CachedWidget>(schema, Load, false, Ct);
            // A synchronous caller arriving meanwhile waits for the same fill.
            var third = Task.Run(() => cache.GetOrLoad<CachedWidget>(schema, () => throw new InvalidOperationException("a second load")), Ct);
            var loaded = Collection<CachedWidget>();
            gate.SetResult(loaded);

            Assert.True(Holds(loaded, await first));
            Assert.True(Holds(loaded, await second));
            Assert.True(Holds(loaded, await third));
            Assert.Equal(1, loads);
        }

        [Fact]
        public async Task GetOrLoadAsync_ACallerThatCancels_LeavesTheFillToTheOthers()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();
            var gate = new TaskCompletionSource<NucleusCollection<CachedWidget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            var leaving = cache.GetOrLoadAsync<CachedWidget>(schema, _ => gate.Task, false, cancelled.Token);
            var staying = cache.GetOrLoadAsync<CachedWidget>(schema, _ => gate.Task, false, Ct);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

            var loaded = Collection<CachedWidget>();
            gate.SetResult(loaded);
            Assert.True(Holds(loaded, await staying));
            Assert.True(cache.IsCached(typeof(CachedWidget)));
        }

        [Fact]
        public async Task GetOrLoadAsync_AFailedLoad_DoesNotPoisonTheCache()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cache.GetOrLoadAsync<CachedWidget>(schema, _ => Task.FromException<NucleusCollection<CachedWidget>>(new InvalidOperationException("down")), false, Ct));
            Assert.False(cache.IsCached(typeof(CachedWidget)));

            var loaded = Collection<CachedWidget>();
            Assert.True(Holds(loaded, await cache.GetOrLoadAsync<CachedWidget>(schema, _ => Task.FromResult(loaded), false, Ct)));
        }

        [Fact]
        public void GetOrLoad_AFailedLoad_ThrowsWhatTheLoaderThrew_AndDoesNotPoisonTheCache()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();

            Assert.Throws<InvalidOperationException>(() => cache.GetOrLoad<CachedWidget>(schema, () => throw new InvalidOperationException("down")));

            var loaded = Collection<CachedWidget>();
            Assert.True(Holds(loaded, cache.GetOrLoad(schema, () => loaded)));
        }

        [Fact]
        public async Task Refresh_ServesTheStaleEntry_WhileTheReloadRuns()
        {
            var clock = new TestClock();
            var cache = new NucleusCache(() => clock.Now);
            var schema = Schema<RefreshingWidget>();
            var stale = Collection<RefreshingWidget>();
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromResult(stale), false, Ct);
            clock.Now = clock.Now.AddSeconds(61); // past the refresh interval of 60

            var gate = new TaskCompletionSource<NucleusCollection<RefreshingWidget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            Task<NucleusCollection<RefreshingWidget>> Reload(CancellationToken _)
            {
                Interlocked.Increment(ref reloads);
                return gate.Task;
            }

            // The read past the interval is answered at once and starts the reload in the background.
            Assert.True(Holds(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct)));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reloads) == 1, TimeSpan.FromSeconds(10)));
            // Reads while it runs still get the stale entry, and start no second reload.
            Assert.True(Holds(stale, cache.GetOrLoad<RefreshingWidget>(schema, () => throw new InvalidOperationException("a load in-line"))));

            var fresh = Collection<RefreshingWidget>();
            gate.SetResult(fresh);
            Assert.True(SpinWait.SpinUntil(
                () => Holds(fresh, cache.GetOrLoad<RefreshingWidget>(schema, () => throw new InvalidOperationException("a load in-line"))),
                TimeSpan.FromSeconds(10)));
            Assert.Equal(1, reloads);
        }

        [Fact]
        public async Task Refresh_ThatFinishesAfterAnEviction_DoesNotBringBackWhatItRead()
        {
            var clock = new TestClock();
            var cache = new NucleusCache(() => clock.Now);
            var schema = Schema<RefreshingWidget>();
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromResult(Collection<RefreshingWidget>()), false, Ct);
            clock.Now = clock.Now.AddSeconds(61);

            var gate = new TaskCompletionSource<NucleusCollection<RefreshingWidget>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => { Interlocked.Increment(ref reloads); return gate.Task; }, false, Ct);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reloads) == 1, TimeSpan.FromSeconds(10)));

            // A save evicts the type while the reload, which read before the save, is still running.
            cache.Evict(typeof(RefreshingWidget));
            var readBeforeTheSave = Collection<RefreshingWidget>();
            gate.SetResult(readBeforeTheSave);
            await Task.Delay(100, Ct); // give the background reload time to try its swap

            var afterTheSave = Collection<RefreshingWidget>();
            Assert.True(Holds(afterTheSave, cache.GetOrLoad(schema, () => afterTheSave)));
        }

        [Fact]
        public void GetOrLoad_ReturnsAListOfTheCallersOwn()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();
            var loaded = Collection<CachedWidget>();

            var mine = cache.GetOrLoad(schema, () => loaded);
            var theirs = cache.GetOrLoad<CachedWidget>(schema, () => throw new InvalidOperationException("a second load"));
            Assert.NotSame(mine, theirs);

            // Neither the caller's list nor the loader's reaches what the cache reads.
            mine.Clear();
            mine.Add(new CachedWidget { Name = "added by one caller" });
            loaded.Clear();

            Assert.True(Holds(Collection(theirs[0]), cache.GetOrLoad<CachedWidget>(schema, () => throw new InvalidOperationException("a second load"))));
            Assert.True(cache.TryGetById<CachedWidget>(theirs[0].ID, out var found));
            Assert.Same(theirs[0], found);
        }

        [Fact]
        public async Task EditingAListTheCacheHandedOut_DuringAStream_DoesNotBreakTheStream()
        {
            // The stream reads the cache's rows directly. When the public GetOrLoad handed out the
            // cache's own list, editing it part-way through a stream threw "Collection was modified".
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("one"), Row("two"), Row("three"));
            var streamed = new List<CachedWidget>();

            await foreach (var widget in manager.Get<CachedWidget>().WithCancellation(Ct))
            {
                streamed.Add(widget);
                var handedOut = manager.Cache.GetOrLoad<CachedWidget>(Schema<CachedWidget>(), () => throw new InvalidOperationException("a second load"));
                handedOut.Clear();
                handedOut.Add(new CachedWidget { Name = "added by a caller" });
            }

            Assert.Equal(["one", "two", "three"], streamed.Select(w => w.Name));
            Assert.Equal(3, manager.Load<CachedWidget>().Count);
        }

        [Fact]
        public async Task Refresh_ThatFails_WaitsOneIntervalBeforeTheNext_AndKeepsServingTheCachedRows()
        {
            var clock = new TestClock();
            var cache = new NucleusCache(() => clock.Now);
            var schema = Schema<RefreshingWidget>();
            var stale = Collection<RefreshingWidget>();
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromResult(stale), false, Ct);
            var attempts = 0;
            var fresh = Collection<RefreshingWidget>();
            var down = true;
            Task<NucleusCollection<RefreshingWidget>> Reload(CancellationToken _)
            {
                Interlocked.Increment(ref attempts);
                return Volatile.Read(ref down)
                    ? Task.FromException<NucleusCollection<RefreshingWidget>>(new InvalidOperationException("the database is down"))
                    : Task.FromResult(fresh);
            }

            // Past the interval of 60 s: the read gets the cached rows and starts a refresh, which fails.
            clock.Now = clock.Now.AddSeconds(61);
            Assert.True(Holds(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct)));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attempts) == 1, TimeSpan.FromSeconds(10)));
            await Task.Delay(50, Ct); // the failure resets the refresh after the attempt is counted

            // Reads straight after, and up to a whole interval after the failure, start no refresh.
            for (var i = 0; i < 5; i++)
                Assert.True(Holds(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct)));
            clock.Now = clock.Now.AddSeconds(59);
            Assert.True(Holds(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct)));
            await Task.Delay(50, Ct);
            Assert.Equal(1, Volatile.Read(ref attempts));

            // One interval after the failure the next read starts one refresh, which succeeds.
            Volatile.Write(ref down, false);
            clock.Now = clock.Now.AddSeconds(1);
            Assert.True(Holds(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct)));
            Assert.True(SpinWait.SpinUntil(
                () => Holds(fresh, cache.GetOrLoad<RefreshingWidget>(schema, () => throw new InvalidOperationException("a load in-line"))),
                TimeSpan.FromSeconds(10)));
            Assert.Equal(2, Volatile.Read(ref attempts));
        }

        [Fact]
        public async Task Refresh_ThatFails_LogsOneWarning()
        {
            var clock = new TestClock();
            var log = new CapturingLogger();
            var cache = new NucleusCache(() => clock.Now) { Logger = log };
            var schema = Schema<RefreshingWidget>();
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromResult(Collection<RefreshingWidget>()), false, Ct);
            var down = new InvalidOperationException("the database is down");

            clock.Now = clock.Now.AddSeconds(61);
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromException<NucleusCollection<RefreshingWidget>>(down), false, Ct);
            Assert.True(SpinWait.SpinUntil(() => log.Count == 1, TimeSpan.FromSeconds(10)));
            await cache.GetOrLoadAsync<RefreshingWidget>(schema, _ => Task.FromException<NucleusCollection<RefreshingWidget>>(down), false, Ct);
            await Task.Delay(50, Ct);

            var entry = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Same(down, entry.Exception);
            Assert.Contains(typeof(RefreshingWidget).FullName!, entry.Message);
        }

        [Fact]
        public void TheManager_GivesTheCacheItsLogger_UnlessItHasOne()
        {
            var mine = new CapturingLogger<ObjectDataManager>();
            var manager = new ObjectDataManager(new DataProviderRegistry([new FakeDataProvider()]), new ObjectSchemaProvider(), mine);
            Assert.Same(mine, manager.Cache.Logger);

            var first = new CapturingLogger();
            var shared = new NucleusCache { Logger = first };
            var second = new ObjectDataManager(new DataProviderRegistry([new FakeDataProvider()]), new ObjectSchemaProvider(), mine, cache: shared);
            Assert.Same(first, second.Cache.Logger);
        }

        private static NucleusCollection<T> Collection<T>(T obj) where T : ICoreObject<T>, new() => [obj];

        /// <summary>Records what is logged, for the refresh failure tests.</summary>
        private class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel Level, Exception? Exception, string Message)> _entries = [];

            public IReadOnlyList<(LogLevel Level, Exception? Exception, string Message)> Entries
            {
                get { lock (_entries) return [.. _entries]; }
            }

            public int Count
            {
                get { lock (_entries) return _entries.Count; }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_entries)
                    _entries.Add((logLevel, exception, formatter(state, exception)));
            }
        }

        private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

        [Fact]
        public void LoadAll_NonCacheableType_AlwaysHitsProvider()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<TestWidget>(Row("uncached"));

            manager.Load<TestWidget>();
            manager.Load<TestWidget>();

            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public void LoadAll_AfterLifetimeExpiry_Reloads()
        {
            var (manager, provider, clock) = NewManager();
            provider.SetupRows<CachedWidget>(Row("expiring"));

            manager.Load<CachedWidget>();
            clock.Now = clock.Now.AddSeconds(301); // past cacheLifetime of 300
            manager.Load<CachedWidget>();

            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public void LoadAll_ForceRefresh_Reloads()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("forced"));

            manager.Load<CachedWidget>();
            manager.Load<CachedWidget>(forceRefresh: true);

            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public void LoadByID_CacheableType_AnswersFromIndexWithoutExtraQuery()
        {
            var (manager, provider, _) = NewManager();
            var id = Guid.NewGuid();
            provider.SetupRows<CachedWidget>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "indexed" });

            manager.Load<CachedWidget>(); // populate
            var found = manager.LoadByID<CachedWidget>(id);
            var missing = manager.LoadByID<CachedWidget>(Guid.NewGuid());

            Assert.NotNull(found);
            Assert.Equal("indexed", found!.Name);
            Assert.Null(missing);
            Assert.Single(provider.Contexts); // index answered both lookups
        }

        [Fact]
        public async Task Save_EvictsCachedCollection()
        {
            var (manager, provider, _) = NewManager();
            provider.SetupRows<CachedWidget>(Row("stale"));
            manager.Load<CachedWidget>();

            await manager.SaveAsync(new CachedWidget { Name = "new-row" });
            manager.Load<CachedWidget>();

            // one load context + one save context + one reload context
            Assert.Equal(3, provider.Contexts.Count);
        }

        [Fact]
        public async Task Delete_EvictsCachedCollection()
        {
            var (manager, provider, _) = NewManager();
            var item = ObjectSchema<TestDeletableWidget>.BuildObjectViaReflection(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid() });
            provider.SetupRows<CachedWidget>(Row("any"));
            manager.Load<CachedWidget>();

            await manager.DeleteAsync(item);

            // TestDeletableWidget is not cacheable, but eviction must not throw and the cached
            // type stays intact (unrelated type deleted).
            Assert.True(manager.Cache.IsCached(typeof(CachedWidget)));
        }
    }
}
