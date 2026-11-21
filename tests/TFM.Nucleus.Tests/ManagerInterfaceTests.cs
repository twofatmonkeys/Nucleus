using Microsoft.Extensions.DependencyInjection;

namespace TFM.Nucleus.Tests
{
    [Deletable]
    public class AlphaDeletableWidget : RootAlpha<AlphaDeletableWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// What a store reaches through <see cref="IObjectDataManager"/>, the only type DI registers.
    /// Saving with a token, saving a batch, deleting, deleting by predicate, subscribing and
    /// opening a caller-owned context used to exist only on the concrete manager, so a store had
    /// to cast or reach through <c>DataProvider</c>, which is the unrooted default provider and
    /// misroutes a rooted type. Every call here goes through an interface-typed variable.
    /// </summary>
    public class ManagerInterfaceTests
    {
        private static (FakeDataProvider Alpha, FakeDataProvider Fallback, IObjectDataManager Data) NewData()
        {
            var alpha = new FakeDataProvider { RootType = typeof(RootAlpha<>) };
            var fallback = new FakeDataProvider();
            IObjectDataManager data = new ObjectDataManager(new DataProviderRegistry([alpha, fallback]), new ObjectSchemaProvider());
            return (alpha, fallback, data);
        }

        private static T Loaded<T>(Guid id) where T : CoreObject =>
            ObjectSchema<T>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id });

        [Fact]
        public void AddNucleusData_RegistersTheInterfaceOnly()
        {
            var services = new ServiceCollection();

            services.AddNucleusData();

            Assert.Contains(services, d => d.ServiceType == typeof(IObjectDataManager));
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(ObjectDataManager));
        }

        [Fact]
        public async Task SaveAsync_WithToken_RoutesToTheOwningProviderAndPassesTheToken()
        {
            var (alpha, fallback, data) = NewData();
            using var cts = new CancellationTokenSource();

            await data.SaveAsync(new AlphaWidget { Name = "a" }, cts.Token);

            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
            Assert.Equal(cts.Token, Assert.Single(Assert.Single(alpha.Contexts).ExecutionTokens));
            Assert.Empty(fallback.Contexts);
        }

        [Fact]
        public async Task SaveAsync_OfABatch_RunsInOneTransaction()
        {
            var (alpha, _, data) = NewData();
            using var cts = new CancellationTokenSource();
            var widgets = new List<AlphaWidget> { new() { Name = "a" }, new() { Name = "b" }, new() { Name = "c" } };

            var saved = await data.SaveAsync(widgets, cts.Token);

            var context = Assert.Single(alpha.Contexts);
            Assert.True(context.HasTransaction);
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(widgets.Cast<object?>(), query.Objects);
            Assert.NotNull(saved);
            Assert.Equal(3, saved!.Count);
            Assert.All(widgets, w => Assert.False(w.IsNew));
        }

        [Fact]
        public async Task SaveAsync_OfAnEmptyBatch_WritesNothing()
        {
            var (alpha, _, data) = NewData();

            Assert.Null(await data.SaveAsync(new List<AlphaWidget>(), CancellationToken.None));
            Assert.Empty(alpha.Contexts);
        }

        [Fact]
        public async Task DeleteAsync_RoutesToTheOwningProviderAndPassesTheToken()
        {
            var (alpha, fallback, data) = NewData();
            using var cts = new CancellationTokenSource();
            var widget = Loaded<AlphaDeletableWidget>(Guid.NewGuid());

            await data.DeleteAsync(widget, cts.Token);

            var context = Assert.Single(alpha.Contexts);
            Assert.True(context.HasTransaction);
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(FakeQueryKind.Delete, query.Kind);
            Assert.Same(widget, Assert.Single(query.Objects));
            Assert.Empty(fallback.Contexts);
        }

        [Fact]
        public async Task DeleteAsync_OfABatch_RunsAsOneDelete()
        {
            var (alpha, _, data) = NewData();
            var widgets = new List<AlphaDeletableWidget> { Loaded<AlphaDeletableWidget>(Guid.NewGuid()), Loaded<AlphaDeletableWidget>(Guid.NewGuid()) };

            await data.DeleteAsync(widgets);

            var query = Assert.IsType<FakeQuery>(Assert.Single(Assert.Single(alpha.Contexts).ExecutedQueries));
            Assert.Equal(widgets.Cast<object?>(), query.Objects);
        }

        [Fact]
        public async Task DeleteWhereAsync_RoutesToTheOwningProvider()
        {
            var (alpha, fallback, data) = NewData();
            alpha.RowsAffectedByDeleteWhere = 4;
            using var cts = new CancellationTokenSource();

            var affected = await data.DeleteWhereAsync<AlphaDeletableWidget>(w => w.Name == "old", cts.Token);

            Assert.Equal(4, affected);
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
            Assert.Equal(typeof(AlphaDeletableWidget), Assert.Single(Assert.Single(alpha.Contexts).DeletedWhere).EntityType);
            Assert.Empty(fallback.Contexts);
        }

        [Fact]
        public async Task CreateContextAsync_OpensOnTheProviderThatOwnsTheType()
        {
            var (alpha, fallback, data) = NewData();
            using var cts = new CancellationTokenSource();

            await using var rooted = await data.CreateContextAsync<AlphaWidget>(ContextOptions.Transactional, cts.Token);
            await using var unrooted = await data.CreateContextAsync<TestWidget>(ContextOptions.Transactional, cts.Token);

            Assert.Same(rooted, Assert.Single(alpha.Contexts));
            Assert.Same(unrooted, Assert.Single(fallback.Contexts));
            Assert.True(rooted.HasTransaction);
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
        }

        [Fact]
        public async Task CreateContextAsync_ReachesTheProviderThatDataProviderMisses()
        {
            var (alpha, fallback, data) = NewData();

#pragma warning disable CS0618 // The obsolete member is the subject of the test.
            Assert.Same(fallback, ((ObjectDataManager)data).DataProvider);
#pragma warning restore CS0618
            await using var context = await data.CreateContextAsync<AlphaWidget>();

            Assert.Same(context, Assert.Single(alpha.Contexts));
        }

        [Fact]
        public async Task SubscribeToChangesAsync_AsksTheProviderThatOwnsTheType()
        {
            var (alpha, fallback, data) = NewData();

            await Assert.ThrowsAsync<NotSupportedException>(() => data.SubscribeToChangesAsync<AlphaWidget>());

            Assert.Equal(typeof(AlphaWidget), Assert.Single(alpha.SubscriptionRequests));
            Assert.Empty(fallback.SubscriptionRequests);
        }

        [Fact]
        public async Task AdaptorSaveAsync_WithToken_SavesTheAddedObjectsInOneTransaction()
        {
            var (alpha, _, data) = NewData();
            using var cts = new CancellationTokenSource();
            var adaptor = data.Get<AlphaWidget>();
            adaptor.AddRange(new AlphaWidget { Name = "a" }, new AlphaWidget { Name = "b" });

            var saved = await adaptor.SaveAsync(cts.Token);

            Assert.Equal(2, saved!.Count());
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
            Assert.Single(Assert.Single(alpha.Contexts).ExecutedQueries);

            // The saved objects are cleared, so a second save writes nothing.
            var again = await adaptor.SaveAsync(cts.Token);
            Assert.NotNull(again);
            Assert.Empty(again!);
            Assert.Single(alpha.Contexts);
        }

        [Fact]
        public async Task AdaptorFindAsync_WithToken_PassesTheToken()
        {
            var (alpha, _, data) = NewData();
            var id = Guid.NewGuid();
            alpha.SetupRows<AlphaWidget>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "found" });
            using var cts = new CancellationTokenSource();

            var found = await data.Get<AlphaWidget>().FindAsync(id, cts.Token);

            Assert.Equal("found", found!.Name);
            Assert.Equal(cts.Token, Assert.Single(alpha.ContextTokens));
        }

        [Fact]
        public async Task ToPagedResultAsync_WorksOnTheAdaptorAndAfterWhere()
        {
            var (alpha, _, data) = NewData();
            alpha.SetupRows<AlphaWidget>(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "a" },
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "b" });
            using var cts = new CancellationTokenSource();

            var all = await data.Get<AlphaWidget>().ToPagedResultAsync(0, 10, cts.Token);
            var filtered = await data.Get<AlphaWidget>().Where(w => w.Name != null).ToPagedResultAsync(0, 10, cts.Token);

            Assert.Equal(2, all.Count);
            Assert.Equal(2, filtered.Count);
            Assert.Equal(2, alpha.Contexts.Count);
            Assert.All(alpha.ContextTokens, t => Assert.Equal(cts.Token, t));
        }

        [Fact]
        public async Task ToPagedResultAsync_OnAnotherQueryable_Throws()
        {
            var source = new List<TestWidget>().AsQueryable();

            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ToPagedResultAsync(0, 10));
        }

        /// <summary>
        /// A hand-written implementation from before these members still compiles, because each
        /// has a default body, and each tells its caller it is not supported rather than doing
        /// something else.
        /// </summary>
        [Fact]
        public async Task AnOlderImplementation_CompilesAndRefusesTheNewMembers()
        {
            IObjectDataManager data = new OlderManager();
            var widget = new TestWidget();

            await Assert.ThrowsAsync<NotSupportedException>(() => data.SaveAsync(widget, CancellationToken.None));
            await Assert.ThrowsAsync<NotSupportedException>(() => data.SaveAsync(new List<TestWidget> { widget }));
            await Assert.ThrowsAsync<NotSupportedException>(() => data.DeleteAsync(widget));
            await Assert.ThrowsAsync<NotSupportedException>(() => data.DeleteAsync(new List<TestWidget> { widget }));
            await Assert.ThrowsAsync<NotSupportedException>(() => data.DeleteWhereAsync<TestWidget>(w => w.Name == "x"));
            await Assert.ThrowsAsync<NotSupportedException>(() => data.SubscribeToChangesAsync<TestWidget>());
            await Assert.ThrowsAsync<NotSupportedException>(() => data.CreateContextAsync<TestWidget>());
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                data.LoadChildCollectionAsync(new TestDeletableParent().Children));
        }

        /// <summary>The interface as it stood in 2.26, implemented and nothing more.</summary>
        private sealed class OlderManager : IObjectDataManager
        {
#pragma warning disable CS0618 // Implementing the obsolete member is the point.
            public IDataProvider DataProvider => throw new NotImplementedException();
#pragma warning restore CS0618
            public IObjectSchemaProvider ObjectSchema => throw new NotImplementedException();
            public DataProviderStatus DataProviderStatus => DataProviderStatus.Ok;
            public Task<DataProviderStatus> InitializeDataProviderAsync() => Task.FromResult(DataProviderStatus.Ok);
            public IObjectDataAdaptor<T> Get<T>() where T : ICoreObject<T>, new() => throw new NotImplementedException();
            public Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new() => Task.CompletedTask;
        }
    }
}
