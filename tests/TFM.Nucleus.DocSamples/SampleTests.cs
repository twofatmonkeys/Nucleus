using Microsoft.Extensions.Configuration;
using TFM.Nucleus.Data.SQL;

namespace PortlyPrimate
{
    /// <summary>
    /// One sanctuary for the whole test process, in a SQLite file of its own that is deleted
    /// afterwards. It is built by the quick start exactly as the guide shows it.
    /// </summary>
    public sealed class SanctuaryFixture : IAsyncLifetime
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"portly-primate-{Guid.NewGuid():N}");
        private ServiceProvider? _provider;

        public IObjectDataManager Data => _provider!.GetRequiredService<IObjectDataManager>();

        public async ValueTask InitializeAsync() =>
            _provider = await Registration.QuickStartAsync(Path.Combine(_folder, "sanctuary.db"));

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null)
                await _provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held by the engine is left for the operating system's temp cleanup.
            }
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SanctuaryCollection : ICollectionFixture<SanctuaryFixture>
    {
        public const string Name = "sanctuary";
    }

    /// <summary>Runs the guide's examples, so an example that compiles but does not work fails here.</summary>
    [Collection(SanctuaryCollection.Name)]
    public class SampleTests(SanctuaryFixture sanctuary)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private Samples Samples => new(sanctuary.Data);

        [Fact]
        public async Task TheGuideRunsFromStartToFinish()
        {
            // In the order a reader meets them: later examples read what earlier ones saved.
            var gerald = await Samples.StaticApiAsync();
            Assert.Equal("Gerald", gerald.Name);

            Assert.True(await Samples.QueriesAsync(Ct) >= 1);

            var order = await Samples.ChildCollectionsAsync(Ct);
            Assert.Equal(2, order.Lines.Count());
            Assert.Equal(360, order.Lines.First(l => l.Variety == "Cavendish").Quantity);
            Assert.Single(order.Lines.First(l => l.Variety == "Cavendish").Substitutions);

            var brenda = await Samples.LazyAndLinkedAsync(Ct);
            Assert.True(await Samples.LoadOptionsAsync(brenda.ID, Ct) >= 3);
            Assert.Equal(2, await Samples.ChildQueryAsync(brenda, Ct));
            Assert.Equal(0, await Samples.RemoveChildAsync(brenda, Ct));
            var reloaded = await sanctuary.Data.LoadByIDAsync<Resident>(brenda.ID, new LoadOptions { Include = [nameof(Resident.Naps)] }, Ct);
            Assert.Empty(reloaded!.Naps);

            Assert.True(await Samples.UniqueViolationAsync(Ct));
            await Samples.WeighWithRetryAsync(gerald, new WeighIn { MonkeyName = "Gerald", WeightKg = 14.8m, Notes = "before the move" }, Ct);
            await Samples.StatementTimeoutAsync(Ct);
            Assert.Equal(1, await Samples.DeletesAsync(Ct));
            Assert.Equal(1, await Samples.SearchAsync(Ct));

            Assert.Equal(1, await Samples.MixedSaveAndStreamAsync(Ct));
            Assert.Equal("Cavendish", (await Samples.CachedAsync(Ct))!.Name);
            var weighIn = (await sanctuary.Data.LoadAsync<WeighIn>(w => w.MonkeyName == "Sir Reginald", Ct)).Single();
            Assert.Equal("no change", weighIn.Notes);

            await sanctuary.Data.SaveAsync(new GiftShopVisit { SessionId = Guid.NewGuid(), OccurredUtc = DateTimeOffset.UtcNow, Path = "/bananas", Kind = VisitKind.Purchase }, Ct);
            Assert.Equal(3, await Samples.RowReadsAsync(DateTimeOffset.UtcNow.AddDays(-1), Ct));

            var desk = new KeeperDesk(sanctuary.Data);
            await desk.WeighAsync(gerald, new WeighIn { MonkeyName = "Gerald", WeightKg = 14.6m, Notes = "Gerald: no change" }, Ct);
            Assert.Equal("Gerald", (await desk.FindAsync(gerald.ID, Ct))!.Name);
        }

        [Fact]
        public async Task ABrokenRule_StopsTheSave()
        {
            var order = new BananaOrder { PlacedUtc = DateTimeOffset.UtcNow };
            order.Lines.Add(new BananaOrderLine { Variety = "Red Dacca", Quantity = 0 });

            await Assert.ThrowsAsync<InvalidOperationException>(() => sanctuary.Data.SaveAsync(order, Ct));
            Assert.Null(await sanctuary.Data.LoadByIDAsync<BananaOrder>(order.ID, Ct));
        }

        [Fact]
        public async Task ChangeNotifications_AreNotOfferedBySqlite() =>
            await Assert.ThrowsAsync<NotSupportedException>(() => Samples.ChangesAsync(sanctuary.Data, Ct));

        [Fact]
        public void TheRegistrationExamples_Register()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nucleus:ReadOnlySchema"] = "false",
                ["Nucleus:AppName"] = "PortlyPrimate.Web",
                ["Database:Server"] = "db01",
                ["Database:Database"] = "PortlyPrimate",
                ["Database:Username"] = "sanctuary",
                ["Database:Password"] = "from-a-secret-store",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            Registration.FromConfiguration(services, configuration);
            using var provider = services.BuildServiceProvider();

            var nucleus = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NucleusOptions>>().Value;
            var database = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SQLConfiguration>>().CurrentValue;

            Assert.Equal("PortlyPrimate.Web", nucleus.AppName);
            Assert.False(nucleus.ReadOnlySchema);
            Assert.Equal("db01", database.Server);
            Assert.Equal("PortlyPrimate", database.Database);

            foreach (Action<IServiceCollection> register in new Action<IServiceCollection>[]
            {
                s => Registration.SqlServer(s, "secret"), s => Registration.Postgres(s, "secret"),
                s => Registration.Sqlite(s), s => Registration.TwoDatabases(s, "secret"), Registration.RetryPolicy,
            })
            {
                var each = new ServiceCollection();
                register(each);
                Assert.Contains(each, d => d.ServiceType == typeof(IDataProvider));
            }
        }
    }
}
