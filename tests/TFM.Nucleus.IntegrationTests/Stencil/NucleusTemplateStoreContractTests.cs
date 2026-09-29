using TFM.Nucleus.Stencil;
using TFM.Stencil.Abstractions;
using TFM.Stencil.Testing;

namespace TFM.Nucleus.IntegrationTests.Stencil
{
    /// <summary>
    /// TFM.Stencil's store contract, every case, against the live test database, plus what the
    /// contract cannot see from the outside: the collection partition, case-insensitive keys, the
    /// rows on disk, and a content row whose record is gone. xunit builds a fresh instance per test,
    /// and each instance works in a collection of its own, so every case starts with an empty store
    /// and no test resets another's rows: no table reset and no cross-process lock are needed.
    /// </summary>
    [Collection("Database")]
    public sealed class NucleusTemplateStoreContractTests(DatabaseFixture fixture) : IAsyncLifetime
    {
        private readonly string _collection = $"t{Guid.NewGuid():N}";
        private readonly List<string> _collections = [];

        public static IEnumerable<object[]> Names => TemplateStoreContract.Cases.Select(c => new object[] { c.Name });

        [Theory]
        [MemberData(nameof(Names))]
        public async Task Contract(string name)
        {
            var store = Store();

            await TemplateStoreContract.Find(name).Run(store, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Collections_AreIsolated_EvenForTheSameKey()
        {
            var mail = Store();
            var paper = Store(_collection + "b");
            await mail.SaveAsync(TemplateStoreContract.NewTemplate("welcome", "Mail welcome"));
            await paper.SaveAsync(TemplateStoreContract.NewTemplate("welcome", "Paper welcome"));
            await mail.SaveFileContentAsync("welcome", "a.css", [1]);
            await paper.SaveFileContentAsync("welcome", "a.css", [2]);

            Assert.Equal("Mail welcome", (await mail.GetAsync("welcome"))!.Name);
            Assert.Equal("Paper welcome", (await paper.GetAsync("welcome"))!.Name);
            Assert.Equal(1, await mail.CountAsync(new TemplateQuery()));
            Assert.Equal(new byte[] { 1 }, await mail.GetFileContentAsync("welcome", "a.css"));

            await mail.DeleteAsync("welcome");

            Assert.Null(await mail.GetAsync("welcome"));
            Assert.Equal("Paper welcome", (await paper.GetAsync("welcome"))!.Name);
            Assert.Equal(new byte[] { 2 }, await paper.GetFileContentAsync("welcome", "a.css"));
        }

        [Fact]
        public async Task Collection_IsCaseInsensitive()
        {
            var lower = Store();
            var upper = Store(_collection.ToUpperInvariant());
            await lower.SaveAsync(TemplateStoreContract.NewTemplate());

            Assert.NotNull(await upper.GetAsync("welcome-letter"));
            Assert.Equal(1, await upper.CountAsync(new TemplateQuery()));
        }

        [Fact]
        public async Task KeysAndPaths_MatchCaseInsensitively_AndKeepTheCallersCasing()
        {
            var store = Store();
            var template = TemplateStoreContract.NewTemplate("Welcome-Letter", "Welcome letter");
            await store.SaveAsync(template);
            await store.SaveFileContentAsync("welcome-letter", "Logo.PNG", [1, 2]);

            Assert.Equal("Welcome-Letter", (await store.GetAsync("WELCOME-LETTER"))!.Key);
            Assert.Equal(new byte[] { 1, 2 }, await store.GetFileContentAsync("Welcome-Letter", "logo.png"));

            // An upsert under another casing is the same template, not a second one.
            await store.SaveAsync(TemplateStoreContract.NewTemplate("welcome-letter", "Renamed"));
            Assert.Equal(1, await store.CountAsync(new TemplateQuery()));
            Assert.Equal("Renamed", (await store.GetAsync("Welcome-Letter"))!.Name);

            var file = Assert.Single(await FilesOf(RecordKey("welcome-letter")));
            Assert.Equal("Logo.PNG", file.Path);
            Assert.Equal($"{_collection}:welcome-letter|logo.png", file.FileKey);
        }

        [Fact]
        public async Task Rows_CarryTheLowercasedCompositeKeys_AndDeleteLeavesNone()
        {
            var manager = fixture.RequireManager();
            var store = Store();
            await store.SaveAsync(TemplateStoreContract.NewTemplate("gift-voucher", "Gift voucher"));
            await store.SaveFileContentAsync("gift-voucher", "template.html", [1]);
            await store.SaveFileContentAsync("gift-voucher", "logo.png", [2]);
            await store.SaveFileContentAsync("gift-voucher", "logo.png", [3]);

            var record = Assert.Single(await manager.Get<StencilTemplateRecord>().Where(r => r.Collection == _collection).ToListAsync());
            Assert.Equal($"{_collection}:gift-voucher", record.RecordKey);
            Assert.Null(record.FilesJson);
            Assert.Equal(2, (await FilesOf(record.RecordKey!)).Count);

            await store.DeleteAsync("gift-voucher");

            Assert.Empty(await manager.Get<StencilTemplateRecord>().Where(r => r.Collection == _collection).ToListAsync());
            Assert.Empty(await FilesOf(record.RecordKey!));
        }

        [Fact]
        public async Task AContentRowWhoseRecordIsGone_IsInvisible_ThenOverwritten_ThenSwept()
        {
            // The row a content save racing a delete can leave: nothing in the database ties it to
            // a record, so the store must cope with it on every path.
            var manager = fixture.RequireManager();
            var store = Store();
            var recordKey = RecordKey("ghost");
            await manager.SaveAsync(new StencilTemplateFile
            {
                RecordKey = recordKey,
                Path = "a.css",
                FileKey = $"{recordKey}|a.css",
                Content = [9, 9],
            });

            Assert.Null(await store.GetAsync("ghost"));
            Assert.Equal(0, await store.CountAsync(new TemplateQuery()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveFileContentAsync("ghost", "a.css", [1]));

            await store.SaveAsync(TemplateStoreContract.NewTemplate("ghost", "Ghost"));
            await store.SaveFileContentAsync("ghost", "a.css", [1]);

            var file = Assert.Single(await FilesOf(recordKey));
            Assert.Equal(new byte[] { 1 }, file.Content);

            await store.DeleteAsync("ghost");
            await manager.SaveAsync(new StencilTemplateFile { RecordKey = recordKey, Path = "b.css", FileKey = $"{recordKey}|b.css", Content = [7] });
            await store.DeleteAsync("ghost");

            Assert.Empty(await FilesOf(recordKey));
        }

        [Fact]
        public async Task AMegabyteOfRandomBytes_RoundTrips()
        {
            var store = Store();
            await store.SaveAsync(TemplateStoreContract.NewTemplate());
            var bytes = new byte[1024 * 1024];
            new Random(20260930).NextBytes(bytes);

            await store.SaveFileContentAsync("welcome-letter", "font.woff2", bytes);

            Assert.Equal(bytes, await store.GetFileContentAsync("welcome-letter", "font.woff2"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("mail:room")]
        [InlineData("mail|room")]
        public void Constructor_RefusesAnUnusableCollection(string collection)
        {
            fixture.RequireManager();

            Assert.ThrowsAny<ArgumentException>(() => new TestStore(fixture.Manager!, collection));
        }

        [Fact]
        public void Constructor_RefusesANullManager()
        {
            Assert.Throws<ArgumentNullException>(() => new TestStore(null!, "mailroom"));
        }

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        /// <summary>Removes every row this instance's collections wrote, by predicate, in one transaction.</summary>
        public async ValueTask DisposeAsync()
        {
            var manager = fixture.Manager;
            if (manager is null || _collections.Count == 0)
                return;

            await using var context = await manager.DataProvider.CreateContextAsync(ContextOptions.Transactional);
            try
            {
                foreach (var collection in _collections.Distinct(StringComparer.Ordinal))
                {
                    var prefix = collection + ":";
                    await context.DeleteWhereAsync<StencilTemplateFile>(f => f.RecordKey!.StartsWith(prefix));
                    await context.DeleteWhereAsync<StencilTemplateRecord>(r => r.Collection == collection);
                }
            }
            catch
            {
                await context.TryRollbackAsync();
                throw;
            }
        }

        private TestStore Store(string? collection = null)
        {
            var manager = fixture.RequireManager();
            var name = collection ?? _collection;
            _collections.Add(name.ToLowerInvariant());
            return new TestStore(manager, name);
        }

        private string RecordKey(string key) => $"{_collection}:{key}".ToLowerInvariant();

        private Task<List<StencilTemplateFile>> FilesOf(string recordKey) =>
            fixture.Manager!.Get<StencilTemplateFile>().Where(f => f.RecordKey == recordKey).ToListAsync();

        /// <summary>The one-line subclass a host writes per consumer.</summary>
        private sealed class TestStore(IObjectDataManager data, string collection)
            : NucleusTemplateStore(data, collection, null);
    }
}
