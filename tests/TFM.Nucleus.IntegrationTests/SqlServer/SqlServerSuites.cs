using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    // Every provider-neutral suite, run against the SQL Server test database.

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerCrudIntegrationTests(SqlServerFixture fixture) : CrudIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerChildCollectionLoadIntegrationTests(SqlServerFixture fixture) : ChildCollectionLoadIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerDeleteWhereIntegrationTests(SqlServerFixture fixture) : DeleteWhereIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerEnumWhereIntegrationTests(SqlServerFixture fixture) : EnumWhereIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerExcludeIntegrationTests(SqlServerFixture fixture) : ExcludeIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerUniqueIndexIntegrationTests(SqlServerFixture fixture) : UniqueIndexIntegrationTests(fixture)
    {
        private readonly SqlServerFixture _fixture = fixture;

        /// <summary>The engine's own error is kept as the inner exception: 2601, duplicate key in a unique index.</summary>
        [Fact]
        public async Task DuplicateValue_KeepsTheSqlServerErrorInside()
        {
            var manager = _fixture.RequireManager();
            var code = $"uq-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));

            var sql = FindInChain<SqlException>(ex);
            Assert.NotNull(sql);
            Assert.Equal(2601, sql!.Number);
            Assert.Contains("UQ_", sql.Message);
        }
    }

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerBulkSaveIntegrationTests(SqlServerFixture fixture) : BulkSaveIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerSaveBenchmarks(SqlServerFixture fixture) : Benchmarks.SaveBenchmarks(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerAttributionStoreTests(SqlServerFixture fixture) : Attribution.NucleusAttributionStoreTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerBinderyStoreContractTests(SqlServerFixture fixture) : Bindery.NucleusBinderyStoreContractTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerS3AssetStoreContractTests(SqlServerFixture fixture) : S3Library.NucleusS3AssetStoreContractTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerTemplateStoreContractTests(SqlServerFixture fixture) : Stencil.NucleusTemplateStoreContractTests(fixture);
}
