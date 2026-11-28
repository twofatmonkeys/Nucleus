namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>
    /// An ordered read places a null below every value, and PostgreSQL uses an index for an order
    /// only when the index places nulls the same way. Its own default is the other way, so every
    /// index Nucleus creates there says so, and one made the old way is not current: the next
    /// start replaces it.
    /// </summary>
    [Collection(PostgresCollection.Name)]
    public class PostgresIndexNullOrderTests(PostgresFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string Table = "tbl_TFM_Nucleus_IntegrationTests_IntRosterLine";
        private const string Index = $"NCI_{Table}_Parent";

        private Task<object?> NullsFirstAsync() => fixture.Database.ScalarAsync(
            $"SELECT pg_index_column_has_property(i.oid, 1, 'nulls_first') FROM pg_class i JOIN pg_namespace n ON n.oid = i.relnamespace " +
            $"WHERE i.relname = '{Index}' AND n.nspname = current_schema()", Ct);

        [Fact]
        public async Task AColumnIndex_PlacesNullsFirst()
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();

            Assert.Equal(true, await NullsFirstAsync());
            Assert.Equal(DataProviderStatus.Ok, await fixture.Provider!.CheckSchemaIsCurrentAsync());
        }

        [Fact]
        public async Task AnIndexMadeWithTheEnginesDefault_IsNotCurrent_AndAStartReplacesIt()
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();
            await fixture.Database.ExecuteAsync($"DROP INDEX \"{Index}\"; CREATE INDEX \"{Index}\" ON \"{Table}\" (\"Parent\" ASC)", Ct);
            try
            {
                Assert.Equal(false, await NullsFirstAsync());
                Assert.NotEqual(DataProviderStatus.Ok, await fixture.Provider!.CheckSchemaIsCurrentAsync());

                using var stack = await fixture.StartStackAsync(_ => { });

                Assert.Equal(DataProviderStatus.Ok, stack.Provider.Status);
                Assert.Equal(true, await NullsFirstAsync());
            }
            finally
            {
                // Leave the index as every other suite expects it, whatever happened above.
                if (await NullsFirstAsync() is not true)
                    using (await fixture.StartStackAsync(_ => { })) { }
            }
        }
    }
}
