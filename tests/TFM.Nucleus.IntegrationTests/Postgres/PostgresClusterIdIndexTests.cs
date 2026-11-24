namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>
    /// A page with no order of its own is ordered by ClusterID, so every table indexes it.
    /// SQL Server's ClusterID is its clustered index and SQLite orders by its rowid key, but a
    /// PostgreSQL identity column has no index unless one is created: without it every page sorts
    /// all the rows it matches.
    /// </summary>
    [Collection(PostgresCollection.Name)]
    public sealed class PostgresClusterIdIndexTests(PostgresFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Table => NucleusEntityTable.For(typeof(IntWidget)).TableName;

        private static string Index => $"CIX_{Table}_ClusterID";

        /// <summary>True when the table has the index, unique and over ClusterID alone.</summary>
        private async Task<bool> IndexedAsync() =>
            Convert.ToInt64(await fixture.Database.ScalarAsync(
                "SELECT count(*) FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_class t ON t.oid = x.indrelid " +
                "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = x.indkey[0] " +
                $"WHERE i.relname = '{Index}' AND t.relname = '{Table}' AND a.attname = 'ClusterID' AND x.indisunique AND x.indnatts = 1", Ct),
                System.Globalization.CultureInfo.InvariantCulture) == 1;

        [Fact]
        public async Task ATable_HasAUniqueIndexOnClusterId()
        {
            fixture.RequireManager();

            Assert.True(await IndexedAsync());
        }

        [Fact]
        public async Task AMissingIndex_IsCreatedAtTheNextStart()
        {
            fixture.RequireManager();
            using var schema = await fixture.SchemaLockAsync();
            await fixture.Database.ExecuteAsync($"DROP INDEX \"public\".\"{Index}\"", Ct);
            try
            {
                Assert.False(await IndexedAsync());

                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.True(await IndexedAsync());
            }
            finally
            {
                await fixture.Database.ExecuteAsync(
                    $"CREATE UNIQUE INDEX IF NOT EXISTS \"{Index}\" ON \"public\".\"{Table}\" (\"ClusterID\")", CancellationToken.None);
            }
        }
    }
}
