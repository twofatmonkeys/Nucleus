using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A table under an old name is adopted, not orphaned: a stack whose naming gives an entity a
    /// new table name renames the one old table it finds (the full-name form when the provider
    /// renames full-name tables, or a [LegacyTableNames] entry) instead of creating an empty one,
    /// and refuses when it finds several. A rename keeps the table's object id, which a rebuild
    /// would change. Every stack here is rooted at <see cref="IntNamingRoot{T}"/>, so a naming
    /// policy reaches no other suite's table, and every test puts the tables back as the fixture
    /// made them, under the schema lock, so a whole-schema check elsewhere still finds them.
    /// </summary>
    public abstract class TableNamingIntegrationTests(DatabaseFixture fixture)
    {
        private const string FullNameTable = "tbl_TFM_Nucleus_IntegrationTests_IntAdoptedItemWithALongName";
        private const string ShortTable = "tbl_IntAdoptedItemWithALongName";

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected DatabaseFixture Fixture { get; } = fixture;

        protected abstract string TableIdSql(string table);

        protected abstract string DeleteAllSql(string table);

        protected abstract string DropColumnsSql(string table, params string[] columns);

        protected abstract string DropTableIfExistsSql(string table);

        /// <summary>Renames a table by hand, leaving its indexes and constraints with the names they had.</summary>
        protected abstract string RenameTableSql(string from, string to);

        /// <summary>A copy of a table's rows in a new table, without its indexes or constraints.</summary>
        protected abstract string CopyTableSql(string from, string to);

        protected abstract string RowCountSql(string table);

        /// <summary>The names of the table's indexes, constraints and triggers, comma separated.</summary>
        protected abstract string OwnedObjectNamesSql(string table);

        protected abstract Task ExecuteAsync(string sql);

        protected abstract Task<object?> ScalarAsync(string sql);

        private async Task<long?> TableIdAsync(string table) => await ScalarAsync(TableIdSql(table)) is { } id ? Convert.ToInt64(id) : null;

        private async Task<long> RowCountAsync(string table) => Convert.ToInt64(await ScalarAsync(RowCountSql(table)));

        private async Task<string[]> OwnedObjectNamesAsync(string table) =>
            (await ScalarAsync(OwnedObjectNamesSql(table)) as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        private Task<NucleusTestStack> StartAsync(Action<RelationalProviderOptions>? configure = null) =>
            Fixture.StartRootedStackAsync(typeof(IntNamingRoot<>), configure ?? (_ => { }));

        /// <summary>
        /// The tables as the fixture made them: the full-name and pinned tables, empty, and none of
        /// the old or short names. A stack under the full-name policy recreates whichever is missing.
        /// </summary>
        private async Task ResetAsync()
        {
            foreach (var table in new[] { ShortTable, IntLegacyItem.OldTable, IntLegacyItem.OlderTable })
                await ExecuteAsync(DropTableIfExistsSql(table));
            using (await StartAsync())
            {
            }
            await ExecuteAsync(DeleteAllSql(FullNameTable));
            await ExecuteAsync(DeleteAllSql(IntLegacyItem.Table));
        }

        private async Task<List<T>> SaveAsync<T>(Func<int, T> make, int count) where T : CoreObject<T>, new()
        {
            var items = Enumerable.Range(0, count).Select(make).ToList();
            using var setup = await StartAsync();
            await setup.Manager.SaveAsync<T>(items);
            return items;
        }

        [Fact]
        public async Task TheFullNameTable_IsRenamedUnderAShorterPolicy_KeepingItsRowsAndRenamingWhatItCarries()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ResetAsync();
            try
            {
                var items = await SaveAsync(i => new IntAdoptedItemWithALongName { Name = $"adopt-{i}", Quantity = i }, 50);
                var tableId = await TableIdAsync(FullNameTable);

                using var stack = await StartAsync(o =>
                {
                    o.TableNaming = TableNamingPolicy.ObjectName(1);
                    o.RenameFullNameTables = true;
                });

                Assert.NotNull(tableId);
                Assert.Equal(tableId, await TableIdAsync(ShortTable));
                Assert.Null(await TableIdAsync(FullNameTable));
                var loaded = await stack.Manager.Get<IntAdoptedItemWithALongName>().Where(i => i.Name != null).ToListAsync(Ct);
                Assert.Equal(items.Select(i => (i.ID, i.Name, i.Quantity)).Order(), loaded.Select(i => (i.ID, i.Name, i.Quantity)).Order());

                // The key, the indexes, the constraints and the triggers carry the new name; none keeps the old one.
                var owned = await OwnedObjectNamesAsync(ShortTable);
                Assert.Contains($"PK_{ShortTable}", owned);
                Assert.Contains($"NCI_{ShortTable}_Name", owned);
                Assert.DoesNotContain(owned, n => n.Contains("TFM_Nucleus", StringComparison.Ordinal));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync(null));

                var added = new IntAdoptedItemWithALongName { Name = "after", Quantity = 99 };
                await stack.Manager.SaveAsync(added);
                Assert.Single(await stack.Manager.Get<IntAdoptedItemWithALongName>().Where(i => i.Name == "after").ToListAsync(Ct));
            }
            finally
            {
                await ResetAsync();
            }
        }

        [Fact]
        public async Task WithoutRenameFullNameTables_AnEmptyTableIsCreated_AndTheOldOneIsLeftAsItIs()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ResetAsync();
            try
            {
                await SaveAsync(i => new IntAdoptedItemWithALongName { Name = $"keep-{i}" }, 5);

                using var stack = await StartAsync(o => o.TableNaming = TableNamingPolicy.ObjectName(1));

                Assert.Equal(0, await RowCountAsync(ShortTable));
                Assert.Equal(5, await RowCountAsync(FullNameTable));
                Assert.Empty(await stack.Manager.Get<IntAdoptedItemWithALongName>().Where(i => i.Name != null).ToListAsync(Ct));
            }
            finally
            {
                await ResetAsync();
            }
        }

        [Fact]
        public async Task AnOldNameTable_IsRenamed_ThenGetsTheColumnItLacks()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ResetAsync();
            try
            {
                var items = await SaveAsync(i => new IntLegacyItem { Name = $"legacy-{i}", Quantity = i }, 20);
                // As an older release left it: under its old name, without a column added since.
                await ExecuteAsync(RenameTableSql(IntLegacyItem.Table, IntLegacyItem.OlderTable));
                await ExecuteAsync(DropColumnsSql(IntLegacyItem.OlderTable, "Quantity"));
                var tableId = await TableIdAsync(IntLegacyItem.OlderTable);

                using var stack = await StartAsync();

                Assert.NotNull(tableId);
                Assert.Equal(tableId, await TableIdAsync(IntLegacyItem.Table));
                Assert.Null(await TableIdAsync(IntLegacyItem.OlderTable));
                var loaded = await stack.Manager.Get<IntLegacyItem>().Where(i => i.Name != null).ToListAsync(Ct);
                Assert.Equal(items.Select(i => (i.ID, i.Name)).Order(), loaded.Select(i => (i.ID, i.Name)).Order());
                Assert.All(loaded, i => Assert.Null(i.Quantity));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync(null));
            }
            finally
            {
                await ResetAsync();
            }
        }

        [Fact]
        public async Task SeveralOldNameTables_AreNotMerged_TheStartFails_AndNothingIsChanged()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ResetAsync();
            try
            {
                await SaveAsync(i => new IntLegacyItem { Name = $"split-{i}" }, 10);
                await ExecuteAsync(CopyTableSql(IntLegacyItem.Table, IntLegacyItem.OldTable));
                await ExecuteAsync(CopyTableSql(IntLegacyItem.Table, IntLegacyItem.OlderTable));
                await ExecuteAsync(DropTableIfExistsSql(IntLegacyItem.Table));

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync());

                Assert.Contains("does not merge", ex.Message);
                Assert.Contains($"{IntLegacyItem.OldTable}, {IntLegacyItem.OlderTable}", ex.Message);
                Assert.Null(await TableIdAsync(IntLegacyItem.Table));
                Assert.Equal(10, await RowCountAsync(IntLegacyItem.OldTable));
                Assert.Equal(10, await RowCountAsync(IntLegacyItem.OlderTable));
            }
            finally
            {
                await ResetAsync();
            }
        }

        [Fact]
        public async Task AnOldTableBesideItsSuccessor_IsLeftAsItIs()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ResetAsync();
            try
            {
                await SaveAsync(i => new IntLegacyItem { Name = $"both-{i}" }, 3);
                await ExecuteAsync(CopyTableSql(IntLegacyItem.Table, IntLegacyItem.OldTable));
                var tableId = await TableIdAsync(IntLegacyItem.Table);

                using var stack = await StartAsync();

                Assert.Equal(tableId, await TableIdAsync(IntLegacyItem.Table));
                Assert.Equal(3, await RowCountAsync(IntLegacyItem.OldTable));
                Assert.Equal(3, (await stack.Manager.Get<IntLegacyItem>().Where(i => i.Name != null).ToListAsync(Ct)).Count);
            }
            finally
            {
                await ResetAsync();
            }
        }
    }
}
