using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests
{
    [Collection("Database")]
    public class UniqueIndexIntegrationTests(DatabaseFixture fixture)
    {
        [Fact]
        public async Task DuplicateValue_SecondSaveFails()
        {
            var manager = fixture.RequireManager();
            var code = $"uq-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));

            // The context wraps provider errors; the SqlException is somewhere in the chain.
            var sql = FindSqlException(ex);
            Assert.NotNull(sql);
            Assert.Equal(2601, sql!.Number);
            Assert.Contains("UQ_", sql.Message);
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

        private static SqlException? FindSqlException(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql)
                    return sql;
            return null;
        }
    }
}
