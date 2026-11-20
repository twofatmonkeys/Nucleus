namespace TFM.Nucleus.IntegrationTests
{
    public abstract class UniqueIndexIntegrationTests(DatabaseFixture fixture)
    {
        [Fact]
        public async Task DuplicateValue_SecondSaveFails()
        {
            var manager = fixture.RequireManager();
            var code = $"uq-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            // Every provider reports the broken index as the neutral exception, naming its column,
            // and the manager's save throws it as it is rather than wrapped.
            var violation = await Assert.ThrowsAsync<UniqueViolationException>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));
            Assert.True(violation.Is<IntUniqueItem>(nameof(IntUniqueItem.Code)));
            Assert.Equal("UQ_tbl_TFM_Nucleus_IntegrationTests_IntUniqueItem_Code", violation.IndexName);
            Assert.NotNull(violation.InnerException);
        }

        [Fact]
        public async Task NullValues_AreNotConstrained()
        {
            var manager = fixture.RequireManager();
            var first = new IntUniqueItem();
            var second = new IntUniqueItem();

            await manager.SaveAsync(first);
            await manager.SaveAsync(second);

            var ids = new[] { first.ID, second.ID };
            var loaded = manager.Get<IntUniqueItem>().Where(x => ids.Contains(x.ID)).ToList();
            Assert.Equal(2, loaded.Count);
            Assert.All(loaded, x => Assert.Null(x.Code));
        }

        [Fact]
        public async Task DistinctValues_BothSave()
        {
            var manager = fixture.RequireManager();
            var prefix = $"dv-{Guid.NewGuid():N}";

            await manager.SaveAsync(new IntUniqueItem { Code = prefix + "-a" });
            await manager.SaveAsync(new IntUniqueItem { Code = prefix + "-b" });

            var loaded = manager.Get<IntUniqueItem>().Where(x => x.Code!.StartsWith(prefix)).ToList();
            Assert.Equal(2, loaded.Count);
        }
    }
}
