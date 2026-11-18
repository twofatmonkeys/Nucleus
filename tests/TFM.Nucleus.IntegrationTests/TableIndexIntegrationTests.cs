namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// End-to-end cover for [TableIndex]: the engine has the index the entity declares, with its
    /// keys in order and its included columns, the schema check reads it back as current, and a
    /// start-up recreates it when it is missing, without touching the table. The table is
    /// <see cref="IntRowItem"/>'s, changed under the schema lock. A unique index is covered on
    /// <see cref="IntPathItem"/>'s table: every fact on it holds the schema lock too, because the
    /// net8.0 and net10.0 runs share the table and some facts take its index away for a while.
    /// </summary>
    public abstract class TableIndexIntegrationTests(DatabaseFixture fixture)
    {
        private const string Table = "tbl_TFM_Nucleus_IntegrationTests_IntRowItem";
        private const string Index = $"NIX_{Table}_BatchAt";
        private const string PathTable = "tbl_TFM_Nucleus_IntegrationTests_IntPathItem";
        private const string PathIndex = $"NIX_{PathTable}_KeyPath";

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected DatabaseFixture Fixture { get; } = fixture;

        protected abstract string DropIndexSql(string table, string index);

        /// <summary>The index's columns as one string: keys in order, then "+" and each included column by name.</summary>
        protected abstract string IndexColumnsSql(string table, string index);

        /// <summary>1 when the index is unique, 0 when it is plain, null when it is missing.</summary>
        protected abstract string IsUniqueSql(string table, string index);

        /// <summary>A plain index with the given name over the given columns, in order.</summary>
        protected abstract string CreatePlainIndexSql(string table, string index, params string[] columns);

        protected abstract Task ExecuteAsync(string sql);

        protected abstract Task<object?> ScalarAsync(string sql);

        [Fact]
        public async Task DeclaredIndex_HasItsKeysInOrderAndItsIncludedColumns()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();

            Assert.Equal("Batch,At,+Amount,+Name,+Notes", await ScalarAsync(IndexColumnsSql(Table, Index)));
            Assert.Equal(DataProviderStatus.Ok, Fixture.Provider!.CheckSchemaIsCurrent(null));
        }

        [Fact]
        public async Task MissingIndex_MakesTheSchemaNotCurrent_AndIsCreatedAgainOnStart()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ExecuteAsync(DropIndexSql(Table, Index));
            try
            {
                Assert.NotEqual(DataProviderStatus.Ok, Fixture.Provider!.CheckSchemaIsCurrent(null));

                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.Equal("Batch,At,+Amount,+Name,+Notes", await ScalarAsync(IndexColumnsSql(Table, Index)));
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync(null));
            }
            finally
            {
                // Leave the index in place for every other suite, whatever happened above.
                if (await ScalarAsync(IndexColumnsSql(Table, Index)) is null or DBNull)
                    using (await Fixture.StartStackAsync(_ => { })) { }
            }
        }

        [Fact]
        public async Task UniqueIndex_SameKeysTwice_ThrowsNamingTheEntityAndTheIndex()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            var templateId = Guid.NewGuid();
            await manager.SaveAsync(new IntPathItem { TemplateId = templateId, FilePath = "a/b.pdf" });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntPathItem { TemplateId = templateId, FilePath = "a/b.pdf" }));

            var violation = ExceptionChain.Find<UniqueViolationException>(ex);
            Assert.NotNull(violation);
            Assert.True(violation!.IsIndex<IntPathItem>("KeyPath"));
            Assert.Equal(PathIndex, violation.IndexName);
            Assert.Equal("KeyPath", violation.DeclaredIndexName);
            Assert.Equal([nameof(IntPathItem.TemplateId), nameof(IntPathItem.FilePath)], violation.PropertyNames);
            Assert.Equal([nameof(IntPathItem.TemplateId), nameof(IntPathItem.FilePath)], violation.ColumnNames);
            Assert.Null(violation.PropertyName);
            Assert.False(violation.Is<IntPathItem>(nameof(IntPathItem.FilePath)));
        }

        [Fact]
        public async Task UniqueIndex_ARowWithANullKey_IsNotConstrained()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            var templateId = Guid.NewGuid();

            await manager.SaveAsync(new IntPathItem { TemplateId = templateId });
            await manager.SaveAsync(new IntPathItem { TemplateId = templateId });

            Assert.Equal(2, manager.Get<IntPathItem>().Where(x => x.TemplateId == templateId).ToList().Count);
        }

        [Fact]
        public async Task UniqueIndex_MadePlainInTheDatabase_IsNotCurrent_AndStartMakesItUniqueAgain()
        {
            Fixture.RequireSettings();
            using var schema = await Fixture.SchemaLockAsync();
            await ExecuteAsync(DropIndexSql(PathTable, PathIndex));
            await ExecuteAsync(CreatePlainIndexSql(PathTable, PathIndex, nameof(IntPathItem.TemplateId), nameof(IntPathItem.FilePath)));
            try
            {
                // The check a read-only schema stops at: it reports the difference and changes nothing.
                Assert.NotEqual(DataProviderStatus.Ok, Fixture.Provider!.CheckSchemaIsCurrent(null));
                Assert.Equal(0, await PathIndexUniqueAsync());

                using var stack = await Fixture.StartStackAsync(_ => { });

                Assert.Equal(1, await PathIndexUniqueAsync());
                Assert.Equal(DataProviderStatus.Ok, await stack.Provider.CheckSchemaIsCurrentAsync(null));
            }
            finally
            {
                if (await PathIndexUniqueAsync() != 1)
                    await RestorePathIndexAsync();
            }
        }

        [Fact]
        public async Task UniqueIndex_OverExistingDuplicates_FailsTheStart_AndChangesNoRows()
        {
            var manager = Fixture.RequireManager();
            using var schema = await Fixture.SchemaLockAsync();
            var templateId = Guid.NewGuid();
            await ExecuteAsync(DropIndexSql(PathTable, PathIndex));
            try
            {
                await manager.SaveAsync(new IntPathItem { TemplateId = templateId, FilePath = "dup.pdf" });
                await manager.SaveAsync(new IntPathItem { TemplateId = templateId, FilePath = "dup.pdf" });

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture.StartStackAsync(_ => { }));

                // The schema manager's own explanation, not only the engine's error, which names both too.
                Assert.Contains($"Cannot create unique index {PathIndex} on [{PathTable}] ([TemplateId], [FilePath])", ex.Message);
                Assert.Contains("No data was modified", ex.Message);
                Assert.Equal(2, manager.Get<IntPathItem>().Where(x => x.TemplateId == templateId).ToList().Count);
                Assert.Null(await PathIndexUniqueAsync());
            }
            finally
            {
                await manager.DeleteWhereAsync<IntPathItem>(x => x.TemplateId == templateId);
                await RestorePathIndexAsync();
            }
        }

        /// <summary>1 when IntPathItem's index is unique, 0 when it is plain, null when it is missing.</summary>
        private async Task<int?> PathIndexUniqueAsync() =>
            await ScalarAsync(IsUniqueSql(PathTable, PathIndex)) is { } value and not DBNull ? Convert.ToInt32(value) : null;

        // Puts IntPathItem's unique index back for every other fact, dropping a plain one in its way.
        private async Task RestorePathIndexAsync()
        {
            if (await PathIndexUniqueAsync() != null)
                await ExecuteAsync(DropIndexSql(PathTable, PathIndex));
            using (await Fixture.StartStackAsync(_ => { })) { }
        }
    }
}
