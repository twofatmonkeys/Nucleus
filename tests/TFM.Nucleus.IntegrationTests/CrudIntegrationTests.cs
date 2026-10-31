namespace TFM.Nucleus.IntegrationTests
{
    public abstract class CrudIntegrationTests(DatabaseFixture fixture)
    {
        [Fact]
        public void SchemaIsCurrentAfterInitialization()
        {
            fixture.RequireSettings();

            var status = fixture.Provider!.CheckSchemaIsCurrent(null);

            Assert.Equal(DataProviderStatus.Ok, status);
        }

        [Fact]
        public async Task InsertAndLoad_RoundTrip()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = $"rt-{Guid.NewGuid():N}", Quantity = 7 };
            var id = widget.ID;

            await manager.SaveAsync(widget);
            var loaded = manager.Get<IntWidget>().Where(w => w.ID == id).ToList();

            var found = Assert.Single(loaded);
            Assert.Equal(widget.Name, found.Name);
            Assert.Equal(7, found.Quantity);
            Assert.False(found.IsNew);
        }

        [Fact]
        public async Task Update_RoundTrip()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = $"upd-{Guid.NewGuid():N}", Quantity = 1 };
            var id = widget.ID;
            await manager.SaveAsync(widget);

            widget.Quantity = 42;
            await manager.SaveAsync(widget);
            var loaded = manager.Get<IntWidget>().Where(w => w.ID == id).ToList();

            Assert.Equal(42, Assert.Single(loaded).Quantity);
        }

        [Fact]
        public async Task RolledBackTransaction_PersistsNothing()
        {
            fixture.RequireSettings();
            var widget = new IntWidget { Name = $"rb-{Guid.NewGuid():N}", Quantity = 1 };
            var id = widget.ID;

            using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional))
            {
                await context.SaveAsync(widget);
                context.TryRollback();
            }

            var loaded = fixture.Manager!.Get<IntWidget>().Where(w => w.ID == id).ToList();
            Assert.Empty(loaded);
        }

        [Fact]
        public async Task HardDelete_RemovesRow()
        {
            var manager = fixture.RequireManager();
            var item = new IntHardItem { Name = $"hd-{Guid.NewGuid():N}" };
            var id = item.ID;
            await manager.SaveAsync(item);

            await manager.DeleteAsync(item);

            var loaded = manager.Get<IntHardItem>().Where(x => x.ID == id).ToList();
            Assert.Empty(loaded);
        }

        [Fact]
        public async Task SoftDelete_FlagsRowAndFiltersReads()
        {
            var manager = fixture.RequireManager();
            var item = new IntSoftItem { Name = $"sd-{Guid.NewGuid():N}" };
            var id = item.ID;
            await manager.SaveAsync(item);

            await manager.DeleteAsync(item);

            // Default reads must not see the row.
            var loaded = manager.Get<IntSoftItem>().Where(x => x.ID == id).ToList();
            Assert.Empty(loaded);

            // IncludeDeleted must see it, flagged.
            var query = fixture.Provider!.ParseSelectExpression<IntSoftItem>(
                ((System.Linq.Expressions.Expression<Func<IntSoftItem, bool>>)(x => x.ID == id)).Body);
            query.IncludeDeleted = true;
            using var context = fixture.Provider.CreateContext();
            var dataSet = context.GetDataSet(query);
            Assert.Equal(1, dataSet.RowCount);
        }

        [Fact]
        public async Task PagedLoad_ReturnsPageAndTotalInOneRoundTrip()
        {
            var manager = fixture.RequireManager();
            var prefix = $"pg-{Guid.NewGuid():N}-";
            for (int i = 0; i < 5; i++)
                await manager.SaveAsync(new IntWidget { Name = $"{prefix}{i}", Quantity = i });
            System.Linq.Expressions.Expression predicate =
                ((System.Linq.Expressions.Expression<Func<IntWidget, bool>>)(w => w.Name!.StartsWith(prefix))).Body;

            var firstPage = await manager.LoadPagedAsync<IntWidget>(0, 2, predicate);
            var lastPage = await manager.LoadPagedAsync<IntWidget>(4, 2, predicate);

            Assert.Equal(2, firstPage.Count);
            Assert.Equal(5, firstPage.TotalCount);
            Assert.True(firstPage.HasMore);
            Assert.Single(lastPage);
            Assert.False(lastPage.HasMore);
        }

        [Fact]
        public async Task Delete_UndecoratedType_Throws()
        {
            var manager = fixture.RequireManager();
            var widget = new IntWidget { Name = "no-delete", Quantity = 0 };
            await manager.SaveAsync(widget);

            await Assert.ThrowsAsync<NotSupportedException>(() => manager.DeleteAsync(widget));
        }
    }
}
