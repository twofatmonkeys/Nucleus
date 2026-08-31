namespace TFM.Nucleus.Tests
{
    // The recommended multi-database root shape: a generic pass-through base per database.
    public abstract class RootAlpha<T> : CoreObject<T> where T : CoreObject<T> { }
    public abstract class RootBeta<T> : CoreObject<T> where T : CoreObject<T> { }

    public class AlphaWidget : RootAlpha<AlphaWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class BetaWidget : RootBeta<BetaWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class MultiProviderTests
    {
        private static (FakeDataProvider Alpha, FakeDataProvider Beta, FakeDataProvider Fallback, DataProviderRegistry Registry) NewRegistry()
        {
            var alpha = new FakeDataProvider { RootType = typeof(RootAlpha<>) };
            var beta = new FakeDataProvider { RootType = typeof(RootBeta<>) };
            var fallback = new FakeDataProvider();
            return (alpha, beta, fallback, new DataProviderRegistry([alpha, beta, fallback]));
        }

        [Fact]
        public void EntityResolvesToItsRootProvider()
        {
            var (alpha, beta, _, registry) = NewRegistry();

            Assert.Same(alpha, registry.GetProvider<AlphaWidget>());
            Assert.Same(beta, registry.GetProvider<BetaWidget>());
        }

        [Fact]
        public void UnrootedEntity_FallsBackToDefaultProvider()
        {
            var (_, _, fallback, registry) = NewRegistry();

            Assert.Same(fallback, registry.GetProvider<TestWidget>());
        }

        [Fact]
        public void NearestRootWins_ForNestedRoots()
        {
            // RootAlpha<T> and CoreObject are both ancestors of AlphaWidget; a provider rooted
            // at the more-derived RootAlpha must win over one rooted at CoreObject.
            var broad = new FakeDataProvider { RootType = typeof(CoreObject) };
            var narrow = new FakeDataProvider { RootType = typeof(RootAlpha<>) };
            var registry = new DataProviderRegistry([broad, narrow]);

            Assert.Same(narrow, registry.GetProvider<AlphaWidget>());
            Assert.Same(broad, registry.GetProvider<BetaWidget>());
        }

        [Fact]
        public void NoRegisteredRootAndNoFallback_Throws()
        {
            var registry = new DataProviderRegistry([new FakeDataProvider { RootType = typeof(RootAlpha<>) }]);

            Assert.Throws<InvalidOperationException>(() => registry.GetProvider<BetaWidget>());
        }

        [Fact]
        public async Task SaveRoutesToTheOwningProvider()
        {
            var (alpha, beta, _, registry) = NewRegistry();
            var manager = new ObjectDataManager(registry, new ObjectSchemaProvider());

            await manager.SaveAsync(new AlphaWidget { Name = "a" });
            await manager.SaveAsync(new BetaWidget { Name = "b" });

            var alphaQuery = Assert.IsType<FakeQuery>(Assert.Single(Assert.Single(alpha.Contexts).ExecutedQueries));
            Assert.Equal(typeof(AlphaWidget), alphaQuery.EntityType);
            var betaQuery = Assert.IsType<FakeQuery>(Assert.Single(Assert.Single(beta.Contexts).ExecutedQueries));
            Assert.Equal(typeof(BetaWidget), betaQuery.EntityType);
        }

        [Fact]
        public void LoadRoutesToTheOwningProvider()
        {
            var (alpha, beta, _, registry) = NewRegistry();
            var manager = new ObjectDataManager(registry, new ObjectSchemaProvider());
            var id = Guid.NewGuid();
            alpha.SetupRows<AlphaWidget>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "routed" });

            var loaded = manager.Get<AlphaWidget>().Where(w => w.ID == id).ToList();

            Assert.Equal("routed", Assert.Single(loaded).Name);
            Assert.Empty(beta.Contexts);
        }

        [Fact]
        public void IsUnderRoot_HandlesOpenGenericAndPlainRoots()
        {
            Assert.True(DataProviderRegistry.IsUnderRoot(typeof(AlphaWidget), typeof(RootAlpha<>)));
            Assert.True(DataProviderRegistry.IsUnderRoot(typeof(AlphaWidget), typeof(CoreObject)));
            Assert.True(DataProviderRegistry.IsUnderRoot(typeof(AlphaWidget), null));
            Assert.False(DataProviderRegistry.IsUnderRoot(typeof(AlphaWidget), typeof(RootBeta<>)));
        }
    }
}
