namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// End-to-end cover for [TableIndex]: the engine has the index the entity declares, with its
    /// keys in order and its included columns, the schema check reads it back as current, and a
    /// start-up recreates it when it is missing, without touching the table. The table is
    /// <see cref="IntRowItem"/>'s, changed under the schema lock.
    /// </summary>
    public abstract class TableIndexIntegrationTests(DatabaseFixture fixture)
    {
        private const string Table = "tbl_TFM_Nucleus_IntegrationTests_IntRowItem";
        private const string Index = $"NIX_{Table}_BatchAt";

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected DatabaseFixture Fixture { get; } = fixture;

        protected abstract string DropIndexSql(string table, string index);

        /// <summary>The index's columns as one string: keys in order, then "+" and each included column by name.</summary>
        protected abstract string IndexColumnsSql(string table, string index);

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
    }
}
