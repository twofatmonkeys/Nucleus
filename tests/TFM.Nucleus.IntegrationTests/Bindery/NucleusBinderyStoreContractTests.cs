using TFM.Bindery;
using TFM.Bindery.Testing;
using TFM.Nucleus.Bindery;

namespace TFM.Nucleus.IntegrationTests.Bindery
{
    /// <summary>
    /// Bindery's page store contract, every case, against the live test database. The cases use
    /// fixed page ids and assume an empty store, so each one starts by emptying the four Bindery
    /// tables under a cross-process lock: the Database collection serialises tests within one
    /// process, and the lock keeps the net8.0 and net10.0 runs, which <c>dotnet test</c> starts
    /// together, from resetting each other's rows.
    /// </summary>
    [Collection("Database")]
    public class NucleusBinderyStoreContractTests(DatabaseFixture fixture)
    {
        public static IEnumerable<object[]> Names => BinderyStoreContract.Cases.Select(c => new object[] { c.Name });

        [Theory]
        [MemberData(nameof(Names))]
        public async Task Contract(string name)
        {
            var manager = fixture.RequireManager();
            using var _ = await TestDatabaseLock.AcquireAsync();
            var store = new NucleusBinderyStore(manager);
            var contractCase = BinderyStoreContract.Find(name);
            Assert.True(contractCase.AppliesTo(store), "the Nucleus store implements every optional capability");

            await ResetAsync(manager);
            await contractCase.Run(store, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Publish_SnapshotsUnderTheRowsOwnId_AndMovesTheToken()
        {
            var manager = fixture.RequireManager();
            using var _ = await TestDatabaseLock.AcquireAsync();
            await ResetAsync(manager);
            var store = new NucleusBinderyStore(manager);
            await store.SaveDraftAsync(BinderyStoreContract.NewPage("pub-1"));
            var before = await store.GetChangeTokenAsync();

            await store.PublishAsync("pub-1");

            var versions = await store.ListVersionsAsync("pub-1");
            var row = Assert.Single(await manager.Get<BinderyPageVersion>().Where(v => v.PageId == "pub-1").ToListAsync());
            Assert.Equal(row.ID.ToString("N"), Assert.Single(versions).VersionId);
            Assert.NotEqual(before, await store.GetChangeTokenAsync());
        }

        [Fact]
        public async Task DiscardingANeverPublishedDraft_RemovesTheRow()
        {
            var manager = fixture.RequireManager();
            using var _ = await TestDatabaseLock.AcquireAsync();
            await ResetAsync(manager);
            var store = new NucleusBinderyStore(manager);
            await store.SaveDraftAsync(BinderyStoreContract.NewPage("gone-1"));

            await store.DiscardDraftAsync("gone-1");

            Assert.Empty(await manager.Get<BinderyDocument>().Where(d => d.PageId == "gone-1").ToListAsync());
            Assert.Empty(await store.ListPagesAsync());
        }

        [Fact]
        public async Task AStorageFailure_SurfacesAsABinderyException_WithTheOriginalAttached()
        {
            fixture.RequireManager();
            var store = new NucleusBinderyStore(new BrokenManager());

            var failure = await Assert.ThrowsAsync<BinderyException>(() => store.ListPagesAsync());

            Assert.NotNull(failure.InnerException);
            Assert.Contains("Listing pages", failure.Message);
        }

        /// <summary>Empties the four tables through a context: the fixture never publishes the static runtime the entity API needs.</summary>
        private static async Task ResetAsync(IObjectDataManager manager)
        {
            var rows = new List<ICoreObject>();
            rows.AddRange(await manager.Get<BinderyPageVersion>().AsQueryable().ToListAsync());
            rows.AddRange(await manager.Get<BinderyDocument>().AsQueryable().ToListAsync());
            rows.AddRange(await manager.Get<BinderyBlockTemplate>().AsQueryable().ToListAsync());
            rows.AddRange(await manager.Get<BinderyChangeToken>().AsQueryable().ToListAsync());
            if (rows.Count == 0)
                return;
            await using var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional);
            foreach (var row in rows)
                await context.DeleteAsync(row);
        }

        /// <summary>A manager whose every query fails the way an unreachable server would.</summary>
        private sealed class BrokenManager : IObjectDataManager
        {
            public IDataProvider DataProvider => throw new InvalidOperationException("no provider");
            public IObjectSchemaProvider ObjectSchema => throw new InvalidOperationException("no schema");
            public DataProviderStatus DataProviderStatus => default;
            public IObjectDataAdaptor<T> Get<T>() where T : ICoreObject<T>, new() => throw new TimeoutException("server unreachable");
            public Task SaveAsync<T>(T obj) where T : ICoreObject<T>, new() => throw new TimeoutException("server unreachable");
            public Task<DataProviderStatus> InitializeDataProviderAsync() => throw new TimeoutException("server unreachable");
        }
    }
}
