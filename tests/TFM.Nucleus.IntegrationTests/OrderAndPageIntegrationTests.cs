namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// OrderBy, ThenBy, Skip and Take on a query, run by each engine: the rows come back in the
    /// order asked for, pages over tied keys or no order at all never share or miss a row, an
    /// unordered cut follows the order the rows were written in, the children of a cut come with
    /// exactly the rows returned, and nulls sort the same way everywhere. The unit tests pin the
    /// SQL; these prove each engine runs it as meant.
    /// </summary>
    public abstract class OrderAndPageIntegrationTests(DatabaseFixture fixture) : IAsyncDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly List<string> _seeded = [];
        private readonly List<IntBundle> _bundles = [];

        /// <summary>Deletes what this test seeded; xunit disposes the class after each test.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var prefix in _seeded)
                await fixture.DeleteWidgetsAsync(prefix, CancellationToken.None);
            if (_bundles.Count > 0)
                await fixture.RequireManager().DeleteAsync(_bundles, CancellationToken.None);
        }

        /// <summary>
        /// One widget per quantity, named by its position, saved one at a time so the order they
        /// were written in is the order given.
        /// </summary>
        private async Task<(IObjectDataManager Data, string Prefix, List<IntWidget> Widgets)> SeedAsync(params int[] quantities)
        {
            IObjectDataManager data = fixture.RequireManager();
            var prefix = $"op-{Guid.NewGuid():N}-";
            _seeded.Add(prefix);
            var widgets = new List<IntWidget>();
            for (var i = 0; i < quantities.Length; i++)
            {
                var widget = new IntWidget { Name = $"{prefix}{i:D2}", Quantity = quantities[i] };
                await data.SaveAsync(widget, Ct);
                widgets.Add(widget);
            }
            return (data, prefix, widgets);
        }

        private static IQueryable<IntWidget> Mine(IObjectDataManager data, string prefix) =>
            data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix));

        private static List<string?> Names(IEnumerable<IntWidget> widgets, string prefix) =>
            [.. widgets.Select(w => w.Name![prefix.Length..])];

        [Fact]
        public async Task OrderByDescendingThenTake_ReturnsTheLargest_InOrder()
        {
            var (data, prefix, _) = await SeedAsync(5, 1, 9, 3, 7);

            var top = await Mine(data, prefix).OrderByDescending(w => w.Quantity).Take(3).ToListAsync(Ct);

            Assert.Equal([9, 7, 5], top.Select(w => w.Quantity));
        }

        [Fact]
        public async Task ThenBy_OrdersTheRowsTheFirstKeyTies()
        {
            var (data, prefix, _) = await SeedAsync(2, 1, 2, 1);

            var rows = await Mine(data, prefix).OrderBy(w => w.Quantity).ThenByDescending(w => w.Name).ToListAsync(Ct);

            Assert.Equal(["03", "01", "02", "00"], Names(rows, prefix));
        }

        [Fact]
        public async Task PagesOverATiedKey_NeverShareOrMissARow()
        {
            // Every row ties on the key, so only the insertion-order tiebreak keeps the pages apart.
            var (data, prefix, widgets) = await SeedAsync([.. Enumerable.Repeat(1, 10)]);

            var seen = new List<Guid>();
            for (var page = 0; page < 4; page++)
                seen.AddRange((await Mine(data, prefix).OrderBy(w => w.Quantity).Skip(page * 3).Take(3).ToListAsync(Ct)).Select(w => w.ID));

            Assert.Equal(widgets.Select(w => w.ID).Order(), seen.Order());
        }

        [Fact]
        public async Task UnorderedPages_FollowTheOrderTheRowsWereWritten()
        {
            var (data, prefix, _) = await SeedAsync(4, 3, 2, 1, 0);

            var first = await Mine(data, prefix).Take(2).ToListAsync(Ct);
            var second = await Mine(data, prefix).Skip(2).Take(2).ToListAsync(Ct);
            var paged = await Mine(data, prefix).ToPagedResultAsync(2, 2, Ct);

            Assert.Equal(["00", "01"], Names(first, prefix));
            Assert.Equal(["02", "03"], Names(second, prefix));
            Assert.Equal(["02", "03"], Names(paged, prefix));
            Assert.Equal(5, paged.TotalCount);
        }

        [Fact]
        public async Task First_OfAnUnorderedQuery_IsTheFirstRowWritten()
        {
            var (data, prefix, _) = await SeedAsync(3, 1, 2);

            Assert.Equal("00", Names([await Mine(data, prefix).FirstAsync(Ct)], prefix).Single());
            Assert.Equal(3, (await Mine(data, prefix).OrderByDescending(w => w.Quantity).FirstAsync(Ct)).Quantity);
            Assert.Equal(2, (await Mine(data, prefix).OrderByDescending(w => w.Quantity).Skip(1).FirstAsync(Ct)).Quantity);
        }

        [Fact]
        public async Task APagedResult_OfAnOrderedQuery_IsAPageOfThatOrder()
        {
            var (data, prefix, _) = await SeedAsync(5, 1, 9, 3, 7);

            var page = await Mine(data, prefix).OrderByDescending(w => w.Quantity).ToPagedResultAsync(1, 2, Ct);

            Assert.Equal([7, 5], page.Select(w => w.Quantity));
            Assert.Equal(5, page.TotalCount);
            Assert.True(page.HasMore);
        }

        [Fact]
        public async Task CountAndAny_OfACutQuery_CountWhatTheCutLeaves()
        {
            var (data, prefix, _) = await SeedAsync(1, 2, 3, 4, 5);

            Assert.Equal(3, await Mine(data, prefix).Skip(2).CountAsync(Ct));
            Assert.Equal(2, await Mine(data, prefix).OrderBy(w => w.Quantity).Take(2).CountAsync(Ct));
            Assert.Equal(1, await Mine(data, prefix).Skip(1).Take(5).Skip(3).CountAsync(Ct));
            Assert.True(await Mine(data, prefix).Skip(4).AnyAsync(Ct));
            Assert.False(await Mine(data, prefix).Skip(5).AnyAsync(Ct));
        }

        [Fact]
        public async Task TheChildrenOfAnOrderedCutOverTies_AreThoseOfTheRowsReturned()
        {
            // Every bundle ties on its name. Without a tiebreak the rows and the children's parent
            // keys could each pick their own two bundles, and a bundle would come back with no files.
            IObjectDataManager data = fixture.RequireManager();
            var name = $"op-{Guid.NewGuid():N}";
            for (var i = 0; i < 5; i++)
            {
                var bundle = new IntBundle { Name = name };
                bundle.Files.Add(new IntBundleFile { Name = $"{i}-a" });
                bundle.Files.Add(new IntBundleFile { Name = $"{i}-b" });
                await data.SaveAsync(bundle, Ct);
                _bundles.Add(bundle);
            }
            var tagOf = _bundles.Select((b, i) => (b.ID, Tag: $"{i}-")).ToDictionary(x => x.ID, x => x.Tag);

            var top = await data.Get<IntBundle>().Where(b => b.Name == name).OrderBy(b => b.Name).Take(2).ToListAsync(Ct);
            var page = await data.Get<IntBundle>().Where(b => b.Name == name).OrderBy(b => b.Name).Skip(2).Take(2).ToListAsync(Ct);

            Assert.Equal(2, top.Count);
            Assert.Equal(2, page.Count);
            Assert.Empty(top.Select(b => b.ID).Intersect(page.Select(b => b.ID)));
            foreach (var bundle in top.Concat(page))
            {
                Assert.Equal(2, bundle.Files.Count);
                Assert.All(bundle.Files, f => Assert.StartsWith(tagOf[bundle.ID], f.Name));
            }
        }

        [Fact]
        public async Task Nulls_SortBelowEveryValue_OnEveryEngine()
        {
            var manager = fixture.RequireManager();
            var batch = $"op-{Guid.NewGuid():N}";
            foreach (var name in new[] { "b", null, "a" })
                await manager.SaveAsync(new IntRowItem { Batch = batch, Name = name, At = DateTimeOffset.UtcNow }, Ct);

            var ascending = await manager.Get<IntRowItem>().Where(r => r.Batch == batch).OrderBy(r => r.Name).ToListAsync(Ct);
            var descending = await manager.Get<IntRowItem>().Where(r => r.Batch == batch).OrderByDescending(r => r.Name).ToListAsync(Ct);
            var rows = await manager.Rows<IntRowItem>(r => r.Batch == batch).Select(r => r.Name).OrderBy(r => r.Name).ToListAsync(Ct);

            Assert.Equal([null, "a", "b"], ascending.Select(r => r.Name));
            Assert.Equal(["b", "a", null], descending.Select(r => r.Name));
            Assert.Equal([null, "a", "b"], rows.Select(r => r.Get<string>("Name")));
        }
    }
}
