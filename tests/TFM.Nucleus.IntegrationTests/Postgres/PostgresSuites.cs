using Npgsql;

namespace TFM.Nucleus.IntegrationTests.Postgres
{
    // Every provider-neutral suite, run against the PostgreSQL test database.

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresCrudIntegrationTests(PostgresFixture fixture) : CrudIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresChildCollectionLoadIntegrationTests(PostgresFixture fixture) : ChildCollectionLoadIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresDeleteWhereIntegrationTests(PostgresFixture fixture) : DeleteWhereIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresEnumWhereIntegrationTests(PostgresFixture fixture) : EnumWhereIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresExcludeIntegrationTests(PostgresFixture fixture) : ExcludeIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresUniqueIndexIntegrationTests(PostgresFixture fixture) : UniqueIndexIntegrationTests(fixture)
    {
        private readonly PostgresFixture _fixture = fixture;

        /// <summary>The engine's own error is kept as the inner exception: SQLSTATE 23505, naming the index.</summary>
        [Fact]
        public async Task DuplicateValue_KeepsThePostgresErrorInside()
        {
            var manager = _fixture.RequireManager();
            var code = $"uq-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));

            var postgres = ExceptionChain.Find<PostgresException>(ex);
            Assert.NotNull(postgres);
            Assert.Equal("23505", postgres!.SqlState);
            Assert.Equal("UQ_tbl_TFM_Nucleus_IntegrationTests_IntUniqueItem_Code", postgres.ConstraintName);
        }
    }

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresBulkSaveIntegrationTests(PostgresFixture fixture) : BulkSaveIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresMixedBatchIntegrationTests(PostgresFixture fixture) : MixedBatchIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresRenamedColumnIntegrationTests(PostgresFixture fixture) : RenamedColumnIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresPatternMatchIntegrationTests(PostgresFixture fixture) : PatternMatchIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresServerValueIntegrationTests(PostgresFixture fixture) : ServerValueIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresSaveBenchmarks(PostgresFixture fixture) : Benchmarks.SaveBenchmarks(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresAttributionStoreTests(PostgresFixture fixture) : Attribution.NucleusAttributionStoreTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresBinderyStoreContractTests(PostgresFixture fixture) : Bindery.NucleusBinderyStoreContractTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresS3AssetStoreContractTests(PostgresFixture fixture) : S3Library.NucleusS3AssetStoreContractTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresTemplateStoreContractTests(PostgresFixture fixture) : Stencil.NucleusTemplateStoreContractTests(fixture);
}
