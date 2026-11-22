namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The async operators called straight on <c>Get&lt;T&gt;()</c>, with no <c>Where</c> first.
    /// The adaptor is both IQueryable&lt;T&gt; and IAsyncEnumerable&lt;T&gt;, and from .NET 10
    /// System.Linq.AsyncEnumerable declares the same names on IAsyncEnumerable&lt;T&gt;, so each
    /// call was ambiguous (CS0121) until the adaptor declared the operator itself. That this file
    /// compiles on net10.0 is most of the test.
    /// </summary>
    public class AdaptorOperatorResolutionTests
    {
        private static readonly Guid FirstId = Guid.NewGuid();
        private static readonly Guid SecondId = Guid.NewGuid();

        private static IObjectDataAdaptor<TestWidget> Widgets(int rows) => Widgets(rows, out _);

        private static IObjectDataAdaptor<TestWidget> Widgets(int rows, out FakeDataProvider provider)
        {
            provider = new FakeDataProvider();
            provider.SetupRows<TestWidget>([.. new[]
            {
                new Dictionary<string, object?> { ["ID"] = FirstId, ["Name"] = "first", ["Quantity"] = 1 },
                new Dictionary<string, object?> { ["ID"] = SecondId, ["Name"] = "second", ["Quantity"] = 2 },
            }.Take(rows)]);
            return new ObjectDataManager(provider, new ObjectSchemaProvider(), null).Get<TestWidget>();
        }

        /// <summary>The one select an operator built. The fake returns every row it holds whatever the Take says.</summary>
        private static FakeQuery OnlySelect(FakeDataProvider provider) =>
            Assert.Single(provider.CreatedQueries.OfType<FakeQuery>(), q => q.Kind == FakeQueryKind.Select);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ListAndArray()
        {
            Assert.Equal([FirstId, SecondId], (await Widgets(2).ToListAsync()).Select(w => w.ID));
            Assert.Equal([FirstId, SecondId], (await Widgets(2).ToListAsync(Ct)).Select(w => w.ID));
            Assert.Equal([FirstId, SecondId], (await Widgets(2).ToArrayAsync(Ct)).Select(w => w.ID));
        }

        [Fact]
        public async Task FirstAndSingle()
        {
            Assert.Equal(FirstId, (await Widgets(2).FirstAsync(Ct)).ID);
            Assert.Equal(FirstId, (await Widgets(2).FirstOrDefaultAsync(Ct))!.ID);
            Assert.Null(await Widgets(0).FirstOrDefaultAsync(Ct));
            Assert.Equal(FirstId, (await Widgets(1).SingleAsync(Ct)).ID);
            Assert.Equal(FirstId, (await Widgets(1).SingleOrDefaultAsync(Ct))!.ID);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Widgets(2).SingleOrDefaultAsync(Ct));

            // The predicate overloads translate their predicate like Where; the fake provider
            // returns its rows whatever the condition, so these check resolution and the shape.
            Assert.NotNull(await Widgets(1).FirstAsync(w => w.Name == "first", Ct));
            Assert.NotNull(await Widgets(1).FirstOrDefaultAsync(w => w.Name == "first", Ct));
            Assert.NotNull(await Widgets(1).SingleAsync(w => w.Name == "first", Ct));
            Assert.NotNull(await Widgets(1).SingleOrDefaultAsync(w => w.Name == "first", Ct));
        }

        [Fact]
        public async Task First_ReadsOneRow()
        {
            await Widgets(2, out var provider).FirstOrDefaultAsync(Ct);
            Assert.Equal(1, OnlySelect(provider).Take);

            await Widgets(2, out provider).FirstAsync(w => w.Name == "first", Ct);
            Assert.Equal(1, OnlySelect(provider).Take);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Widgets(0).FirstAsync(Ct));
        }

        [Fact]
        public async Task Single_ReadsTwoRows()
        {
            await Widgets(1, out var provider).SingleAsync(Ct);
            Assert.Equal(2, OnlySelect(provider).Take);

            await Widgets(1, out provider).SingleOrDefaultAsync(w => w.Name == "first", Ct);
            Assert.Equal(2, OnlySelect(provider).Take);

            Assert.Null(await Widgets(0).SingleOrDefaultAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Widgets(0).SingleAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Widgets(2).SingleAsync(Ct));
        }

        [Fact]
        public async Task First_KeepsTheQuerysExclusionsAndDeletedRows()
        {
            var provider = new FakeDataProvider();
            var data = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);

            await data.Get<TestSoftParent>().Exclude(p => p.Children).IncludeDeleted().Where(p => p.Name == "a").FirstOrDefaultAsync(Ct);

            var select = OnlySelect(provider);
            Assert.Equal(1, select.Take);
            Assert.True(select.IncludeDeleted);
            Assert.Equal(["Children"], select.ExcludedCollections);
        }

        [Fact]
        public async Task AnyAndCount()
        {
            // Any and Count are row reads, which need the provider's own select; the fake has none,
            // so these check that the operators resolve and reach the row read rather than
            // enumerating the rows. Their results are tested against every provider in
            // ManagerLoadIntegrationTests.
            await Assert.ThrowsAsync<NotSupportedException>(() => Widgets(2).AnyAsync(Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => Widgets(1).AnyAsync(w => w.Quantity > 0, Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => Widgets(2).CountAsync(Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => Widgets(2).CountAsync(w => w.Quantity > 0, Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => Widgets(2).LongCountAsync(Ct));

            // A predicate that matches nothing never reaches the database.
            Assert.False(await Widgets(2).AnyAsync(w => false, Ct));
            Assert.Equal(0, await Widgets(2).CountAsync(w => false, Ct));
        }

        [Fact]
        public async Task AnyAndCount_StillCheckTheCollectionNames()
        {
            var data = new ObjectDataManager(new FakeDataProvider(), new ObjectSchemaProvider(), null);
            var query = data.Get<TestWidget>().Where(w => w.Quantity > 0);

            // TestWidget has no child collections, so naming one is a mistake a load reports.
            IObjectDataAdaptor<TestWidget> excluding = new ObjectDataAdaptor<TestWidget>(data, ((IQueryable<TestWidget>)query).Expression, ["NotACollection"]);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => excluding.AnyAsync(Ct));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => excluding.CountAsync(Ct));
        }

        [Fact]
        public async Task Enumeration()
        {
            var ids = new List<Guid>();
            await foreach (var widget in Widgets(2).WithCancellation(Ct))
                ids.Add(widget.ID);
            Assert.Equal([FirstId, SecondId], ids);

            ids.Clear();
            await foreach (var widget in Widgets(2).AsAsyncEnumerable().WithCancellation(Ct))
                ids.Add(widget.ID);
            Assert.Equal([FirstId, SecondId], ids);
        }
    }
}
