using TFM.Nucleus.S3Library;
using TFM.S3Library.Abstractions;
using TFM.S3Library.Testing;

namespace TFM.Nucleus.IntegrationTests.S3Library
{
    /// <summary>
    /// TFM.S3Library's store contract, every case, against the live test database. The cases use
    /// fixed paths with fresh ids and assume an empty store, so each one starts by emptying the
    /// three tables under a cross-process lock: the Database collection serialises tests within
    /// one process, and the lock keeps the net8.0 and net10.0 runs from resetting each other's
    /// rows. The facts below cover what the contract cannot see from the outside: the claim row,
    /// the live-path column, and how a real unique violation is reported.
    /// </summary>
    public abstract class NucleusS3AssetStoreContractTests(DatabaseFixture fixture)
    {
        private const string Suite = "s3library";

        public static IEnumerable<object[]> Names => S3AssetStoreContract.Cases.Select(c => new object[] { c.Name });

        [Theory]
        [MemberData(nameof(Names))]
        public async Task Contract(string name)
        {
            var manager = fixture.RequireManager();
            using var _ = await fixture.LockAsync(Suite);
            await ResetAsync(manager);

            await S3AssetStoreContract.Find(name).Run(new NucleusS3AssetStore(manager), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task EverySave_LeavesOneClaimRowPerAsset_AndDeleteRemovesIt()
        {
            var manager = fixture.RequireManager();
            using var _ = await fixture.LockAsync(Suite);
            await ResetAsync(manager);
            var store = new NucleusS3AssetStore(manager);
            var asset = S3AssetStoreContract.NewAsset();

            await store.SaveAsync(asset);
            asset.AltText = "second";
            await store.SaveAsync(asset);

            var claims = await manager.Get<S3AssetVersionClaim>().Where(c => c.AssetId == asset.Id).ToListAsync();
            var claim = Assert.Single(claims);
            Assert.Equal($"{asset.Id:N}:2", claim.Claim);

            await store.DeleteAsync(asset.Id);

            Assert.Empty(await manager.Get<S3AssetVersionClaim>().Where(c => c.AssetId == asset.Id).ToListAsync());
            Assert.Empty(await manager.Get<S3AssetPreviousPath>().Where(p => p.AssetId == asset.Id).ToListAsync());
        }

        [Fact]
        public async Task SoftDelete_ClearsTheLivePath_AndKeepsTheFullPath()
        {
            var manager = fixture.RequireManager();
            using var _ = await fixture.LockAsync(Suite);
            await ResetAsync(manager);
            var store = new NucleusS3AssetStore(manager);
            var asset = S3AssetStoreContract.NewAsset("photos", "tree.jpg");
            await store.SaveAsync(asset);

            asset.IsDeleted = true;
            asset.DeletedUtc = DateTimeOffset.UtcNow;
            await store.SaveAsync(asset);

            var row = Assert.Single(await manager.Get<S3AssetRecord>().Where(r => r.AssetId == asset.Id).ToListAsync());
            Assert.Equal("photos/tree.jpg", row.FullPath);
            Assert.Null(row.LivePath);
            Assert.True(row.SoftDeleted);

            asset.IsDeleted = false;
            asset.DeletedUtc = null;
            await store.SaveAsync(asset);

            row = Assert.Single(await manager.Get<S3AssetRecord>().Where(r => r.AssetId == asset.Id).ToListAsync());
            Assert.Equal("photos/tree.jpg", row.LivePath);
        }

        [Fact]
        public async Task AClaimAlreadyHeldForTheNextVersion_IsReportedAsConcurrency()
        {
            // Proves the real path: the read-phase check passes, the claim insert hits the unique
            // index, and the provider reports it as a UniqueViolationException the store classifies.
            var manager = fixture.RequireManager();
            using var _ = await fixture.LockAsync(Suite);
            await ResetAsync(manager);
            var store = new NucleusS3AssetStore(manager);
            var asset = S3AssetStoreContract.NewAsset();
            await store.SaveAsync(asset);
            await manager.SaveAsync(new S3AssetVersionClaim { AssetId = asset.Id, Claim = $"{asset.Id:N}:2" });

            asset.AltText = "racing";
            var thrown = await Assert.ThrowsAsync<S3AssetConcurrencyException>(() => store.SaveAsync(asset));

            Assert.Equal(asset.Id, thrown.Id);
            Assert.Equal(1, thrown.ExpectedVersion);
            Assert.Equal(1, thrown.ActualVersion);
            Assert.Equal(1, asset.Version);
            var row = Assert.Single(await manager.Get<S3AssetRecord>().Where(r => r.AssetId == asset.Id).ToListAsync());
            Assert.Null(row.AltText);
        }

        [Fact]
        public async Task AStorageFailure_SurfacesAsAnS3LibraryException_WithTheOriginalAttached()
        {
            fixture.RequireManager();
            var store = new NucleusS3AssetStore(new BrokenManager());

            var failure = await Assert.ThrowsAsync<S3LibraryException>(() => store.GetByIdAsync(Guid.NewGuid()));

            Assert.IsType<TimeoutException>(failure.InnerException);
            Assert.Contains("Reading the asset", failure.Message);
        }

        [Fact]
        public void FindUniqueViolation_TellsConflictFromConcurrency_ByEntityAndColumn()
        {
            // The violation the providers report, wrapped the way a context wraps a failure: the
            // classifier matches the entity and column of the broken index, never an engine type.
            var engine = new InvalidOperationException("engine error");
            Exception Wrapped(Type? type, string? column) => new Exception("Unable to execute SQL on DB.",
                new UniqueViolationException("Unable to execute SQL on DB.", $"UQ_x_{column}", type, column, engine));
            var claim = Wrapped(typeof(S3AssetVersionClaim), nameof(S3AssetVersionClaim.Claim));
            var assetId = Wrapped(typeof(S3AssetRecord), nameof(S3AssetRecord.AssetId));
            var path = Wrapped(typeof(S3AssetRecord), nameof(S3AssetRecord.LivePath));
            var other = Wrapped(null, null);
            var id = Guid.NewGuid();

            var claimViolation = NucleusS3AssetStore.FindUniqueViolation(claim);
            var assetIdViolation = NucleusS3AssetStore.FindUniqueViolation(assetId);
            var pathViolation = NucleusS3AssetStore.FindUniqueViolation(path);
            var otherViolation = NucleusS3AssetStore.FindUniqueViolation(other);

            Assert.NotNull(claimViolation);
            Assert.NotNull(pathViolation);
            Assert.NotNull(otherViolation);
            Assert.Null(NucleusS3AssetStore.FindUniqueViolation(new Exception("Error returning Query from DB.", engine)));
            Assert.Null(NucleusS3AssetStore.FindUniqueViolation(new InvalidOperationException("plain")));

            var concurrency = Assert.IsType<S3AssetConcurrencyException>(NucleusS3AssetStore.ClassifyUniqueViolation(claimViolation!, id, 3, 4, "a/b.jpg"));
            Assert.Equal(id, concurrency.Id);
            Assert.Equal(3, concurrency.ExpectedVersion);
            Assert.Equal(4, concurrency.ActualVersion);
            Assert.IsType<S3AssetConcurrencyException>(NucleusS3AssetStore.ClassifyUniqueViolation(assetIdViolation!, id, 3, 4, "a/b.jpg"));
            var conflict = Assert.IsType<S3AssetConflictException>(NucleusS3AssetStore.ClassifyUniqueViolation(pathViolation!, id, 3, 4, "a/b.jpg"));
            Assert.Equal("a/b.jpg", conflict.FullPath);
            Assert.Null(NucleusS3AssetStore.ClassifyUniqueViolation(otherViolation!, id, 3, 4, "a/b.jpg"));
        }
        [Fact]
        public void SqlPredicate_CombinesOnlyTheTranslatableClauses()
        {
            Assert.Null(NucleusS3AssetStore.SqlPredicateFor(new S3AssetQuery { IncludeDeleted = true, NameContains = "x" }));

            var predicate = NucleusS3AssetStore.SqlPredicateFor(new S3AssetQuery { Folder = "/Photos/", Sha256 = "ABC", HasSupersededBlobs = true })!.Compile();
            Assert.True(predicate(new S3AssetRecord { Folder = "photos", Sha256 = "abc", HasSupersededBlobs = true, SoftDeleted = false }));
            Assert.False(predicate(new S3AssetRecord { Folder = "photos", Sha256 = "abc", HasSupersededBlobs = true, SoftDeleted = true }));
            Assert.False(predicate(new S3AssetRecord { Folder = "other", Sha256 = "abc", HasSupersededBlobs = true }));
        }

        /// <summary>Empties the three tables through a context: the fixture never publishes the static runtime the entity API needs.</summary>
        private static async Task ResetAsync(IObjectDataManager manager)
        {
            var rows = new List<ICoreObject>();
            rows.AddRange(await manager.Get<S3AssetPreviousPath>().AsQueryable().ToListAsync());
            rows.AddRange(await manager.Get<S3AssetVersionClaim>().AsQueryable().ToListAsync());
            rows.AddRange(await manager.Get<S3AssetRecord>().AsQueryable().ToListAsync());
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
