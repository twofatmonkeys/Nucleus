using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// The loads and the mixed-type save on <see cref="IObjectDataManager"/>, the interface DI
    /// registers: by ID, by predicate, paged and streamed, with <see cref="LoadOptions"/>.
    /// </summary>
    public abstract class ManagerLoadIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private async Task<(IObjectDataManager Data, string Prefix, List<IntWidget> Widgets)> SeedAsync(int count)
        {
            IObjectDataManager data = fixture.RequireManager();
            var prefix = $"ml-{Guid.NewGuid():N}-";
            var widgets = Enumerable.Range(0, count).Select(i => new IntWidget { Name = $"{prefix}{i}", Quantity = i }).ToList();
            await data.SaveAsync(widgets, Ct);
            return (data, prefix, widgets);
        }

        [Fact]
        public async Task LoadByIDAsync_ReturnsTheRow_AndNullForAnUnknownID()
        {
            var (data, _, widgets) = await SeedAsync(1);

            var found = await data.LoadByIDAsync<IntWidget>(widgets[0].ID, Ct);
            var missing = await data.LoadByIDAsync<IntWidget>(Guid.NewGuid(), Ct);

            Assert.Equal(widgets[0].Name, found!.Name);
            Assert.Null(missing);
        }

        [Fact]
        public async Task LoadAsync_TakesAPredicate_AndReturnsAnEmptyCollectionForNoMatch()
        {
            var (data, prefix, _) = await SeedAsync(3);

            var matched = await data.LoadAsync<IntWidget>(w => w.Name!.StartsWith(prefix) && w.Quantity > 0, Ct);
            var none = await data.LoadAsync<IntWidget>(w => w.Name == prefix + "nobody", Ct);

            Assert.Equal(2, matched.Count());
            Assert.NotNull(none);
            Assert.Empty(none);
        }

        [Fact]
        public async Task AnUntypedLoad_AcceptsTheWholeLambda_NotOnlyItsBody()
        {
            // The untyped overloads took a predicate's Body; the lambda itself compiled and then
            // failed to translate.
            var (_, prefix, _) = await SeedAsync(2);
            var manager = fixture.RequireManager();
            Expression<Func<IntWidget, bool>> mine = w => w.Name!.StartsWith(prefix);

            var whole = await manager.LoadAsync<IntWidget>((Expression)mine, Ct);
            var body = await manager.LoadAsync<IntWidget>(mine.Body, Ct);

            Assert.Equal(2, whole!.Count());
            Assert.Equal(2, body!.Count());
        }

        [Fact]
        public async Task LoadPagedAsync_ReturnsThePageAndTheTotal()
        {
            var (data, prefix, _) = await SeedAsync(5);

            var page = await data.LoadPagedAsync<IntWidget>(1, 2, w => w.Name!.StartsWith(prefix), cancellationToken: Ct);

            Assert.Equal(2, page.Count);
            Assert.Equal(5, page.TotalCount);
        }

        [Fact]
        public async Task LoadStreamAsync_YieldsEveryMatch()
        {
            var (data, prefix, _) = await SeedAsync(4);
            var names = new List<string?>();

            await foreach (var widget in data.LoadStreamAsync<IntWidget>(w => w.Name!.StartsWith(prefix), cancellationToken: Ct))
                names.Add(widget.Name);

            Assert.Equal(4, names.Count);
        }

        [Fact]
        public async Task IncludeDeleted_SeesSoftDeletedRows_OnALoadAndOnAQuery()
        {
            IObjectDataManager data = fixture.RequireManager();
            var item = new IntSoftItem { Name = $"ml-sd-{Guid.NewGuid():N}" };
            var id = item.ID;
            await data.SaveAsync(item, Ct);
            await data.DeleteAsync(item, Ct);
            var deletedToo = new LoadOptions { IncludeDeleted = true };

            Assert.Null(await data.LoadByIDAsync<IntSoftItem>(id, Ct));
            Assert.NotNull(await data.LoadByIDAsync<IntSoftItem>(id, deletedToo, Ct));
            Assert.Empty(await data.LoadAsync<IntSoftItem>(x => x.ID == id, Ct));
            Assert.Single(await data.LoadAsync<IntSoftItem>(x => x.ID == id, deletedToo, Ct));
            Assert.Empty(await data.Get<IntSoftItem>().Where(x => x.ID == id).ToListAsync(Ct));
            Assert.Single(await data.Get<IntSoftItem>().IncludeDeleted().Where(x => x.ID == id).ToListAsync(Ct));
            Assert.Single(await data.Get<IntSoftItem>().Where(x => x.ID == id).IncludeDeleted().ToListAsync(Ct));
        }

        [Fact]
        public async Task SaveAsync_OfMixedTypes_WritesEachToItsOwnTable()
        {
            IObjectDataManager data = fixture.RequireManager();
            var tag = $"ml-mx-{Guid.NewGuid():N}";
            var widget = new IntWidget { Name = tag, Quantity = 1 };
            var item = new IntHardItem { Name = tag };

            await data.SaveAsync(new ICoreObject?[] { widget, null, item }, Ct);

            Assert.False(widget.IsNew);
            Assert.False(item.IsNew);
            Assert.NotNull(await data.LoadByIDAsync<IntWidget>(widget.ID, Ct));
            Assert.NotNull(await data.LoadByIDAsync<IntHardItem>(item.ID, Ct));
        }
    }
}
