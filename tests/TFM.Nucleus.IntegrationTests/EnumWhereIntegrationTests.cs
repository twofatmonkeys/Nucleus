namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// End-to-end cover for enum columns in a predicate. The unit tests pin the rendered SQL; these
    /// put it in front of SQL Server, which is the only place the bound value's type is proven.
    /// </summary>
    public abstract class EnumWhereIntegrationTests(DatabaseFixture fixture)
    {
        /// <summary>
        /// Saves one Premium row carrying a nullable kind and two Standard rows without one, under a
        /// prefix unique to the call so the shared database needs no cleanup.
        /// </summary>
        private static async Task<string> SeedAsync(ObjectDataManager manager)
        {
            var prefix = $"ek-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntKindWidget
            {
                Name = $"{prefix}-premium",
                Kind = IntWidgetKind.Premium,
                OptionalKind = IntWidgetKind.Standard,
            });
            await manager.SaveAsync(new IntKindWidget { Name = $"{prefix}-a", Kind = IntWidgetKind.Standard });
            await manager.SaveAsync(new IntKindWidget { Name = $"{prefix}-b", Kind = IntWidgetKind.Standard });
            return prefix;
        }

        [Fact]
        public async Task EnumEquals_ReturnsMatchingRow()
        {
            var manager = fixture.RequireManager();
            var prefix = await SeedAsync(manager);

            var loaded = manager.Get<IntKindWidget>()
                .Where(w => w.Name!.StartsWith(prefix) && w.Kind == IntWidgetKind.Premium)
                .ToList();

            var found = Assert.Single(loaded);
            Assert.Equal($"{prefix}-premium", found.Name);
            Assert.Equal(IntWidgetKind.Premium, found.Kind);
        }

        [Fact]
        public async Task NullableEnumIsNull_ReturnsRowsWithoutAValue()
        {
            var manager = fixture.RequireManager();
            var prefix = await SeedAsync(manager);

            var loaded = manager.Get<IntKindWidget>()
                .Where(w => w.Name!.StartsWith(prefix) && w.OptionalKind == null)
                .ToList();

            Assert.Equal(2, loaded.Count);
            Assert.All(loaded, w => Assert.Null(w.OptionalKind));
        }

        [Fact]
        public async Task EnumContains_RendersInAndReturnsBothKinds()
        {
            var manager = fixture.RequireManager();
            var prefix = await SeedAsync(manager);
            var kinds = new[] { IntWidgetKind.Standard, IntWidgetKind.Premium };

            var loaded = manager.Get<IntKindWidget>()
                .Where(w => w.Name!.StartsWith(prefix) && kinds.Contains(w.Kind))
                .ToList();

            Assert.Equal(3, loaded.Count);
            Assert.Equal(1, loaded.Count(w => w.Kind == IntWidgetKind.Premium));
        }

        [Fact]
        public async Task NullableEnum_RoundTripsThroughTheDatabase()
        {
            var manager = fixture.RequireManager();
            var prefix = await SeedAsync(manager);

            var found = Assert.Single(manager.Get<IntKindWidget>()
                .Where(w => w.Name == $"{prefix}-premium")
                .ToList());

            Assert.Equal(IntWidgetKind.Standard, found.OptionalKind);
        }
    }
}
