namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A list of values in a predicate (<c>ids.Contains(w.ID)</c>), run by each engine at lengths
    /// that once failed or had to be cut up by the caller: more values than a command takes
    /// parameters, ordered, cut and counted in the database, over IDs, text and numbers. The unit
    /// tests pin the SQL; these prove each engine binds the list and compares it as meant.
    /// </summary>
    public abstract class ListFilterIntegrationTests(DatabaseFixture fixture) : IAsyncDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>More than SQL Server's 2,100 parameters a command, and than any provider's own limit here.</summary>
        private const int Many = 3000;

        private readonly List<string> _seeded = [];
        private readonly List<string> _items = [];

        public async ValueTask DisposeAsync()
        {
            foreach (var prefix in _seeded)
                await fixture.DeleteWidgetsAsync(prefix, CancellationToken.None);
            foreach (var prefix in _items)
                await fixture.RequireManager().DeleteWhereAsync<IntHardItem>(i => i.Name!.StartsWith(prefix), CancellationToken.None);
        }

        private async Task<(IObjectDataManager Data, string Prefix, List<IntWidget> Widgets)> SeedAsync(int count)
        {
            IObjectDataManager data = fixture.RequireManager();
            var prefix = $"lf-{Guid.NewGuid():N}-";
            _seeded.Add(prefix);
            var widgets = Enumerable.Range(0, count).Select(i => new IntWidget { Name = $"{prefix}{i:D5}", Quantity = i }).ToList();
            await data.SaveAsync(widgets, Ct);
            return (data, prefix, widgets);
        }

        [Fact]
        public async Task ALongListOfIds_Loads_Orders_Cuts_AndCounts()
        {
            var (data, prefix, widgets) = await SeedAsync(Many + 100);
            // Every third row is left out, and IDs of no row are mixed in.
            var wanted = widgets.Where((_, i) => i % 3 != 0).ToList();
            var ids = wanted.Select(w => w.ID).Concat(Enumerable.Range(0, Many - wanted.Count + 500).Select(_ => Guid.NewGuid())).ToList();
            Assert.True(ids.Count > Many);

            var loaded = await data.Get<IntWidget>().Where(w => ids.Contains(w.ID)).ToListAsync(Ct);
            Assert.Equal(wanted.Select(w => w.ID).Order(), loaded.Select(w => w.ID).Order());

            var top = await data.Get<IntWidget>().Where(w => ids.Contains(w.ID)).OrderByDescending(w => w.Quantity).Skip(10).Take(5).ToListAsync(Ct);
            Assert.Equal(wanted.OrderByDescending(w => w.Quantity).Skip(10).Take(5).Select(w => w.Quantity), top.Select(w => w.Quantity));

            Assert.Equal(wanted.Count, await data.Get<IntWidget>().Where(w => ids.Contains(w.ID)).CountAsync(Ct));

            // The list beside another term, and behind an OR.
            var some = await data.Get<IntWidget>().Where(w => ids.Contains(w.ID) && w.Quantity < 10).ToListAsync(Ct);
            Assert.Equal(wanted.Where(w => w.Quantity < 10).Select(w => w.ID).Order(), some.Select(w => w.ID).Order());
            var first = widgets[0].Name;
            var either = await data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix) && (ids.Contains(w.ID) || w.Name == first)).CountAsync(Ct);
            Assert.Equal(wanted.Count + 1, either);
        }

        [Fact]
        public async Task ALongListOfText_AndOfNumbers_MatchTheirRows()
        {
            var (data, prefix, widgets) = await SeedAsync(Many);
            var names = widgets.Where((_, i) => i % 2 == 0).Select(w => w.Name).ToList();
            var quantities = Enumerable.Range(100, 400).ToArray();

            var byName = await data.Get<IntWidget>().Where(w => names.Contains(w.Name)).ToListAsync(Ct);
            Assert.Equal(names.Order(), byName.Select(w => w.Name).Order());

            var byQuantity = await data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix) && quantities.Contains(w.Quantity)).ToListAsync(Ct);
            Assert.Equal(quantities, byQuantity.Select(w => w.Quantity).Order());

            // 64-bit values against an int column.
            var wide = quantities.Select(q => (long)q).ToList();
            Assert.Equal(quantities.Length, await data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix) && wide.Contains(w.Quantity)).CountAsync(Ct));
        }

        [Fact]
        public async Task AShortList_WithRepeatsAndNulls_MatchesEachRowOnce()
        {
            var (data, _, widgets) = await SeedAsync(5);
            var ids = new List<Guid> { widgets[1].ID, widgets[3].ID, widgets[1].ID, Guid.NewGuid() };
            var names = new List<string?> { widgets[0].Name, null, widgets[0].Name };

            var byId = await data.Get<IntWidget>().Where(w => ids.Contains(w.ID)).ToListAsync(Ct);
            var byName = await data.Get<IntWidget>().Where(w => names.Contains(w.Name)).ToListAsync(Ct);

            Assert.Equal(new[] { widgets[1].ID, widgets[3].ID }.Order(), byId.Select(w => w.ID).Order());
            Assert.Equal(widgets[0].ID, Assert.Single(byName).ID);
        }

        [Fact]
        public async Task AnEmptyList_MatchesNothing_AndItsNegationIsRefusedOnADelete()
        {
            var (data, prefix, _) = await SeedAsync(3);
            var none = new List<Guid>();

            Assert.Empty(await data.Get<IntWidget>().Where(w => none.Contains(w.ID)).ToListAsync(Ct));
            Assert.Equal(0, await data.Get<IntWidget>().Where(w => w.Name!.StartsWith(prefix) && none.Contains(w.ID)).CountAsync(Ct));
            Assert.Equal(0, await data.DeleteWhereAsync<IntHardItem>(i => none.Contains(i.ID), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => data.DeleteWhereAsync<IntHardItem>(i => !none.Contains(i.ID), Ct));
        }

        [Fact]
        public async Task ManyObjects_AreDeleted_AndByALongListToo()
        {
            IObjectDataManager data = fixture.RequireManager();
            var prefix = $"lf-{Guid.NewGuid():N}-";
            _items.Add(prefix);
            var items = Enumerable.Range(0, Many).Select(i => new IntHardItem { Name = $"{prefix}{i:D5}" }).ToList();
            await data.SaveAsync(items, Ct);

            // By object: more than one statement took when each ID was a parameter.
            await data.DeleteAsync(items.Take(1200).ToList(), Ct);
            Assert.Equal(Many - 1200, await data.Get<IntHardItem>().Where(i => i.Name!.StartsWith(prefix)).CountAsync(Ct));

            // By predicate, over a list longer than a command takes parameters.
            var ids = items.Skip(1200).Select(i => i.ID).Take(Many - 1300).Concat(Enumerable.Range(0, 800).Select(_ => Guid.NewGuid())).ToList();
            Assert.Equal(Many - 1300, await data.DeleteWhereAsync<IntHardItem>(i => ids.Contains(i.ID), Ct));
            Assert.Equal(100, await data.Get<IntHardItem>().Where(i => i.Name!.StartsWith(prefix)).CountAsync(Ct));
        }
    }
}
