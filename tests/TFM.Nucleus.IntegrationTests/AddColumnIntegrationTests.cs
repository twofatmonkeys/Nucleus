namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A table that only lacks columns has them added in place: its rows are never copied, so a
    /// large table takes no longer than an empty one. Each test takes a populated table back to an
    /// older shape with raw DDL, starts a second stack on the same database, and tells an in-place
    /// change from a rebuild by the table's object id, which only a rebuild changes. A column of
    /// the wrong type still rebuilds. The tables are this suite's own, changed under the schema lock.
    /// </summary>
    public abstract class AddColumnIntegrationTests(DatabaseFixture fixture)
    {
        private const string GrowingTable = "tbl_TFM_Nucleus_IntegrationTests_IntGrowingItem";
        private const string SoftTable = "tbl_TFM_Nucleus_IntegrationTests_IntGrowingSoftItem";
        private const int Rows = 500;

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected DatabaseFixture Fixture { get; } = fixture;

        /// <summary>Drops columns from a table, with any index or default constraint that depends on them.</summary>
        protected abstract string DropColumnsSql(string table, params string[] columns);

        /// <summary>Changes a column to a varchar of the given length.</summary>
        protected abstract string NarrowColumnSql(string table, string column, int length);

        /// <summary>A query returning the table's object id as a bigint.</summary>
        protected abstract string TableIdSql(string table);

        protected abstract string DeleteAllSql(string table);

        protected abstract Task ExecuteAsync(string sql);

        protected abstract Task<object?> ScalarAsync(string sql);

        private async Task<long> TableIdAsync(string table) => Convert.ToInt64(await ScalarAsync(TableIdSql(table)));

        [Fact]
        public async Task NewColumns_AreAddedInPlace_KeepingEveryRow()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            await ExecuteAsync(DeleteAllSql(GrowingTable));
            var items = Enumerable.Range(0, Rows).Select(i => new IntGrowingItem { Name = $"row-{i}", Key = $"key-{i}", Extra = i }).ToList();
            await manager.SaveAsync(items);
            await ExecuteAsync(DropColumnsSql(GrowingTable, "Key", "Extra"));
            var tableId = await TableIdAsync(GrowingTable);
            try
            {
                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.Equal(tableId, await TableIdAsync(GrowingTable));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync());

                var loaded = await stack.Manager.Get<IntGrowingItem>().Where(i => i.Name!.StartsWith("row-")).ToListAsync(Ct);
                Assert.Equal(items.Select(i => i.ID).Order(), loaded.Select(i => i.ID).Order());
                Assert.Equal(items.Select(i => i.Name).Order(), loaded.Select(i => i.Name).Order());
                Assert.All(loaded, i => Assert.Null(i.Key));
                Assert.All(loaded, i => Assert.Null(i.Extra));

                var first = loaded[0];
                first.Key = "added";
                first.Extra = 42;
                await stack.Manager.SaveAsync(first);
                var reloaded = Assert.Single(await stack.Manager.Get<IntGrowingItem>().Where(i => i.Key == "added").ToListAsync(Ct));
                Assert.Equal(first.ID, reloaded.ID);
                Assert.Equal(42, reloaded.Extra);
            }
            finally
            {
                await ExecuteAsync(DeleteAllSql(GrowingTable));
            }
        }

        [Fact]
        public async Task MissingSoftDeleteColumns_AreAddedInPlace_AndSoftDeleteWorks()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            await ExecuteAsync(DeleteAllSql(SoftTable));
            var items = Enumerable.Range(0, 20).Select(i => new IntGrowingSoftItem { Name = $"soft-{i}" }).ToList();
            await manager.SaveAsync(items);
            await ExecuteAsync(DropColumnsSql(SoftTable, SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn));
            var tableId = await TableIdAsync(SoftTable);
            try
            {
                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.Equal(tableId, await TableIdAsync(SoftTable));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync());
                Assert.Equal(items.Count, (await stack.Manager.Get<IntGrowingSoftItem>().Where(i => i.Name!.StartsWith("soft-")).ToListAsync(Ct)).Count);

                await stack.Manager.DeleteWhereAsync<IntGrowingSoftItem>(i => i.Name == "soft-0", Ct);

                var remaining = await stack.Manager.Get<IntGrowingSoftItem>().Where(i => i.Name!.StartsWith("soft-")).ToListAsync(Ct);
                Assert.Equal(items.Count - 1, remaining.Count);
                Assert.DoesNotContain(remaining, i => i.Name == "soft-0");
            }
            finally
            {
                await ExecuteAsync(DeleteAllSql(SoftTable));
            }
        }

        [Fact]
        public async Task ATypeChange_StillRebuilds_KeepingEveryRow()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            await ExecuteAsync(DeleteAllSql(GrowingTable));
            var items = Enumerable.Range(0, Rows).Select(i => new IntGrowingItem { Name = $"row-{i}", Key = $"key-{i}", Extra = i }).ToList();
            await manager.SaveAsync(items);
            await ExecuteAsync(NarrowColumnSql(GrowingTable, "Name", 50));
            var tableId = await TableIdAsync(GrowingTable);
            try
            {
                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.NotEqual(tableId, await TableIdAsync(GrowingTable));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync());

                var loaded = await stack.Manager.Get<IntGrowingItem>().Where(i => i.Name!.StartsWith("row-")).ToListAsync(Ct);
                Assert.Equal(items.Select(i => (i.ID, i.Name, i.Key, i.Extra)).Order(), loaded.Select(i => (i.ID, i.Name, i.Key, i.Extra)).Order());
            }
            finally
            {
                await ExecuteAsync(DeleteAllSql(GrowingTable));
            }
        }
    }
}
