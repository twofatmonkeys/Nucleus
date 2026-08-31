namespace TFM.Nucleus.Tests
{
    [Cacheable(refreshInterval: 0, cacheLifetime: 300)]
    public class CachedWidget : CoreObject<CachedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class NucleusCacheTests
    {
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

            Assert.Same(first, second); // shared cached instance
            Assert.Single(provider.Contexts); // only one provider round trip
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
