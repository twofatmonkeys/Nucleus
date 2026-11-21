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

        private static IObjectDataAdaptor<TestWidget> Widgets(int rows)
        {
            var provider = new FakeDataProvider();
            provider.SetupRows<TestWidget>([.. new[]
            {
                new Dictionary<string, object?> { ["ID"] = FirstId, ["Name"] = "first", ["Quantity"] = 1 },
                new Dictionary<string, object?> { ["ID"] = SecondId, ["Name"] = "second", ["Quantity"] = 2 },
            }.Take(rows)]);
            return new ObjectDataManager(provider, new ObjectSchemaProvider(), null).Get<TestWidget>();
        }

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
        public async Task AnyAndCount()
        {
            Assert.True(await Widgets(2).AnyAsync(Ct));
            Assert.False(await Widgets(0).AnyAsync(Ct));
            Assert.True(await Widgets(1).AnyAsync(w => w.Quantity > 0, Ct));
            Assert.Equal(2, await Widgets(2).CountAsync(Ct));
            Assert.Equal(2, await Widgets(2).CountAsync(w => w.Quantity > 0, Ct));
            Assert.Equal(2L, await Widgets(2).LongCountAsync(Ct));
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
