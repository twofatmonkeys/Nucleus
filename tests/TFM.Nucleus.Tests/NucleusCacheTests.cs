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

            Assert.Same(loaded, await first);
            Assert.Same(loaded, await second);
            Assert.Same(loaded, await third);
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
            Assert.Same(loaded, await staying);
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
            Assert.Same(loaded, await cache.GetOrLoadAsync<CachedWidget>(schema, _ => Task.FromResult(loaded), false, Ct));
        }

        [Fact]
        public void GetOrLoad_AFailedLoad_ThrowsWhatTheLoaderThrew_AndDoesNotPoisonTheCache()
        {
            var cache = new NucleusCache();
            var schema = Schema<CachedWidget>();

            Assert.Throws<InvalidOperationException>(() => cache.GetOrLoad<CachedWidget>(schema, () => throw new InvalidOperationException("down")));

            var loaded = Collection<CachedWidget>();
            Assert.Same(loaded, cache.GetOrLoad(schema, () => loaded));
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
            Assert.Same(stale, await cache.GetOrLoadAsync<RefreshingWidget>(schema, Reload, false, Ct));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reloads) == 1, TimeSpan.FromSeconds(10)));
            // Reads while it runs still get the stale entry, and start no second reload.
            Assert.Same(stale, cache.GetOrLoad<RefreshingWidget>(schema, () => throw new InvalidOperationException("a load in-line")));

            var fresh = Collection<RefreshingWidget>();
            gate.SetResult(fresh);
            Assert.True(SpinWait.SpinUntil(
                () => ReferenceEquals(fresh, cache.GetOrLoad<RefreshingWidget>(schema, () => throw new InvalidOperationException("a load in-line"))),
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
            Assert.Same(afterTheSave, cache.GetOrLoad(schema, () => afterTheSave));
        }

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
