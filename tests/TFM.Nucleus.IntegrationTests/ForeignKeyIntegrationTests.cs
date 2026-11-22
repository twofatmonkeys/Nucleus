using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Foreign keys end to end: every reference the model implies has a key in the database, a
    /// row pointing at a row that is not there is refused, a save writes the rows pointed at
    /// first, a hard delete clears what soft-deleted rows point at, a rebuild of a table keys
    /// point at keeps its rows and the keys, and a key over rows that already point at nothing
    /// is created unchecked. The tables are this suite's own; a test that changes them with raw
    /// DDL or starts a second stack holds the schema lock.
    /// </summary>
    public abstract class ForeignKeyIntegrationTests(DatabaseFixture fixture)
    {
        private const string KeeperTable = "tbl_TFM_Nucleus_IntegrationTests_IntKeyKeeper";
        private const string ResidentTable = "tbl_TFM_Nucleus_IntegrationTests_IntKeyResident";
        private const string ResidentKeeperKey = "FK_" + ResidentTable + "_Keeper";

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected DatabaseFixture Fixture { get; } = fixture;

        /// <summary>Changes a column to a varchar of the given length.</summary>
        protected abstract string NarrowColumnSql(string table, string column, int length);

        /// <summary>A query returning the table's object id as a bigint.</summary>
        protected abstract string TableIdSql(string table);

        protected abstract string DropForeignKeySql(string table, string key);

        /// <summary>A query returning 1 when the key exists and covers every row, 0 when it exists unchecked.</summary>
        protected abstract string KeyIsCheckedSql(string key);

        /// <summary>
        /// False for an engine whose keys are part of their table's definition, so a test cannot
        /// drop one by hand to write a row that points at nothing.
        /// </summary>
        protected virtual bool KeysCanBeDroppedByHand => true;

        /// <summary>False for an engine whose key violation does not name the key.</summary>
        protected virtual bool EngineNamesTheKey => true;

        /// <summary>The violation names the resident's keeper reference, where the engine names the key.</summary>
        private void AssertNamesResidentKeeper(ForeignKeyViolationException ex)
        {
            Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, ex.Kind);
            Assert.False(ex.IsTransient);
            if (!EngineNamesTheKey)
            {
                Assert.Null(ex.EntityType);
                Assert.False(ex.Is<IntKeyResident>(nameof(IntKeyResident.Keeper)));
                return;
            }
            Assert.Equal(ResidentKeeperKey, ex.KeyName);
            Assert.Equal(typeof(IntKeyResident), ex.EntityType);
            Assert.Equal(nameof(IntKeyResident.Keeper), ex.PropertyName);
            Assert.Equal(typeof(IntKeyKeeper), ex.ReferencedType);
            Assert.True(ex.Is<IntKeyResident>(nameof(IntKeyResident.Keeper)));
        }

        protected abstract Task ExecuteAsync(string sql);

        protected abstract Task<object?> ScalarAsync(string sql);

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        private ObjectDataManager Manager()
        {
            var manager = Fixture.RequireManager();
            Assert.SkipUnless(
                manager.Providers.GetProvider(typeof(IntKeyResident)) is RelationalDataProvider provider
                    && provider.Schema.GetTableSchema(typeof(IntKeyResident))?.ForeignKeys.Count > 0,
                "This provider creates no foreign keys.");
            return manager;
        }

        private async Task CleanUpAsync(ObjectDataManager manager, string name)
        {
            await manager.DeleteWhereAsync<IntKeyResident>(r => r.Name!.StartsWith(name), Ct);
            await manager.DeleteWhereAsync<IntKeySoftReport>(r => r.Text!.StartsWith(name), Ct);
            await manager.DeleteWhereAsync<IntKeyMentor>(m => m.Name!.StartsWith(name), Ct);
            await manager.DeleteWhereAsync<IntKeyKeeper>(k => k.Name!.StartsWith(name), Ct);
        }

        [Fact]
        public async Task EveryReference_HasAKeyInTheDatabase_AndTheSchemaIsCurrent()
        {
            var manager = Manager();
            using var schema = await Fixture.SchemaLockAsync();
            var provider = (RelationalDataProvider)manager.Providers.GetProvider(typeof(IntKeyResident));

            Assert.Contains(provider.Schema.GetTableSchema(typeof(IntKeyResident))!.ForeignKeys, k => k.Column.ColumnName == "Keeper");
            Assert.Contains(provider.Schema.GetTableSchema(typeof(IntKeyMentor))!.ForeignKeys, k => k.Column.ColumnName == "Mentor");
            Assert.Equal(DataProviderStatus.Ok, await provider.CheckSchemaIsCurrentAsync());
        }

        [Fact]
        public async Task ARowPointingAtARowThatIsNotThere_IsRefused_AndNothingIsWritten()
        {
            var manager = Manager();
            using var schema = await Fixture.SchemaLockAsync();
            var name = Tag("fk-orphan");
            // Not forceSave: the keeper is never written, so the resident would point at nothing.
            var resident = new IntKeyResident { Name = name, Keeper = new IntKeyKeeper { Name = name } };

            var ex = await Assert.ThrowsAsync<ForeignKeyViolationException>(() => manager.SaveAsync(resident, Ct));

            AssertNamesResidentKeeper(ex);
            Assert.Equal(DatabaseOperation.Write, ex.Operation);
            Assert.Equal(0, await manager.Rows<IntKeyResident>(r => r.Name == name).CountAsync(Ct));
        }

        [Fact]
        public async Task ARefusedDelete_NamesTheReferenceThatStillPointsAtTheRow()
        {
            var manager = Manager();
            using var schema = await Fixture.SchemaLockAsync();
            var name = Tag("fk-named");
            var keeper = new IntKeyKeeper { Name = name };
            await manager.SaveAsync(keeper, Ct);
            await manager.SaveAsync(new IntKeyResident { Name = name, Keeper = keeper }, Ct);
            try
            {
                var ex = await Assert.ThrowsAsync<ForeignKeyViolationException>(() => manager.DeleteAsync(keeper, Ct));

                // The side that points: the resident's reference, not the keeper being deleted.
                AssertNamesResidentKeeper(ex);
                Assert.Equal(1, await manager.Rows<IntKeyKeeper>(k => k.ID == keeper.ID).CountAsync(Ct));
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task ASaveOfSeveralTypes_WritesTheRowsPointedAtFirst_WhateverTheOrderGiven()
        {
            var manager = Manager();
            var name = Tag("fk-order");
            var keeper = new IntKeyKeeper { Name = name };
            var resident = new IntKeyResident { Name = name, Keeper = keeper };
            try
            {
                using (var context = await manager.CreateContextAsync<IntKeyResident>(token: Ct))
                {
                    await context.SaveAsync([resident, keeper], Ct);
                    await context.CommitAsync(Ct);
                }

                var loaded = Assert.Single(await manager.Get<IntKeyResident>().Where(r => r.Name == name).ToListAsync(Ct));
                Assert.Equal(keeper.ID, loaded.Keeper!.ID);
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task RowsOfABatchThatPointAtEachOther_AreInsertedAfterTheRowsTheyPointAt()
        {
            var manager = Manager();
            var name = Tag("fk-chain");
            // Each mentors the next; saved last first, so every row comes before the row it points at.
            var chain = new List<IntKeyMentor>();
            for (var i = 0; i < 400; i++)
                chain.Add(new IntKeyMentor { Name = $"{name}-{i:D3}", Mentor = i == 0 ? null : chain[i - 1] });
            chain.Reverse();
            try
            {
                await manager.SaveAsync<IntKeyMentor>(chain, Ct);

                Assert.Equal(chain.Count, await manager.Rows<IntKeyMentor>(m => m.Name!.StartsWith(name)).CountAsync(Ct));
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task AHardDelete_ClearsWhatSoftDeletedRowsPointAt_ButALiveRowStillStopsIt()
        {
            var manager = Manager();
            using var schema = await Fixture.SchemaLockAsync();
            var name = Tag("fk-soft");
            var gone = new IntKeyKeeper { Name = $"{name}-gone" };
            var kept = new IntKeyKeeper { Name = $"{name}-kept" };
            await manager.SaveAsync<IntKeyKeeper>([gone, kept], Ct);
            var deletedReport = new IntKeySoftReport { Text = $"{name}-deleted", Keeper = gone };
            var liveReport = new IntKeySoftReport { Text = $"{name}-live", Keeper = kept };
            await manager.SaveAsync<IntKeySoftReport>([deletedReport, liveReport], Ct);
            try
            {
                await manager.DeleteAsync(deletedReport, Ct);

                await manager.DeleteAsync(gone, Ct);
                var stored = Assert.Single(await manager.LoadAsync<IntKeySoftReport>(r => r.ID == deletedReport.ID,
                    new LoadOptions { IncludeDeleted = true }, Ct));
                Assert.Null(stored.Keeper);

                var ex = await Assert.ThrowsAnyAsync<DatabaseException>(() => manager.DeleteAsync(kept, Ct));
                Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, ex.Kind);
            }
            finally
            {
                await manager.DeleteAsync(liveReport, Ct);
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task ARebuildOfATableKeysPointAt_KeepsItsRows_AndTheKeys()
        {
            var manager = Manager();
            using var schema = await Fixture.SchemaLockAsync();
            var name = Tag("fk-rebuild");
            var keeper = new IntKeyKeeper { Name = name };
            await manager.SaveAsync(keeper, Ct);
            await manager.SaveAsync(new IntKeyResident { Name = name, Keeper = keeper }, Ct);
            await ExecuteAsync(NarrowColumnSql(KeeperTable, "Name", 100));
            var tableId = Convert.ToInt64(await ScalarAsync(TableIdSql(KeeperTable)));
            try
            {
                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.NotEqual(tableId, Convert.ToInt64(await ScalarAsync(TableIdSql(KeeperTable))));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync());
                var loaded = Assert.Single(await stack.Manager.Get<IntKeyResident>().Where(r => r.Name == name).ToListAsync(Ct));
                Assert.Equal(name, loaded.Keeper!.Name);
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(KeyIsCheckedSql(ResidentKeeperKey))));
                var ex = await Assert.ThrowsAnyAsync<DatabaseException>(() => stack.Manager.DeleteAsync(loaded.Keeper, Ct));
                Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, ex.Kind);
            }
            finally
            {
                await CleanUpAsync(manager, name);
            }
        }

        [Fact]
        public async Task AKeyOverRowsThatPointAtNothing_IsCreatedUnchecked_WithAWarning_AndChecksNewRows()
        {
            var manager = Manager();
            Assert.SkipUnless(KeysCanBeDroppedByHand, "This engine keeps keys in their table's definition; SqliteForeignKeyUpgradeTests covers rows that point at nothing.");
            using var schema = await Fixture.SchemaLockAsync();
            var name = Tag("fk-unchecked");
            await ExecuteAsync(DropForeignKeySql(ResidentTable, ResidentKeeperKey));
            try
            {
                // Without the key the database takes a row pointing at a keeper that was never written.
                await manager.SaveAsync(new IntKeyResident { Name = $"{name}-orphan", Keeper = new IntKeyKeeper { Name = name } }, Ct);

                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(KeyIsCheckedSql(ResidentKeeperKey))));
                Assert.Contains(stack.Log.Entries, e => e.Contains(ResidentKeeperKey) && e.Contains("without checking"));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync());
                var ex = await Assert.ThrowsAnyAsync<DatabaseException>(() =>
                    stack.Manager.SaveAsync(new IntKeyResident { Name = $"{name}-new", Keeper = new IntKeyKeeper { Name = name } }, Ct));
                Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, ex.Kind);
            }
            finally
            {
                await CleanUpAsync(manager, name);
                // The next start checks the rows again, now that none points at nothing.
                await ExecuteAsync(DropForeignKeySql(ResidentTable, ResidentKeeperKey));
                using var restored = await Fixture.StartStackAsync(_ => { });
            }
        }
    }
}
