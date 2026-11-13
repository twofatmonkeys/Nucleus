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
    public sealed class SqlServerRowQueryIntegrationTests(SqlServerFixture fixture) : RowQueryIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerTableIndexIntegrationTests(SqlServerFixture fixture) : TableIndexIntegrationTests(fixture)
    {
        protected override string DropIndexSql(string table, string index) => $"DROP INDEX [{index}] ON [dbo].[{table}]";

        // FOR XML PATH rather than STRING_AGG, which the test database's compatibility level lacks.
        protected override string IndexColumnsSql(string table, string index) =>
            "SELECT STUFF((SELECT ',' + CASE WHEN ic.is_included_column = 1 THEN '+' ELSE '' END + c.name " +
            "FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            $"WHERE i.object_id = OBJECT_ID(N'dbo.{table}') AND i.name = N'{index}' " +
            "ORDER BY ic.is_included_column, ic.key_ordinal, c.name FOR XML PATH('')), 1, 1, '')";

        protected override Task ExecuteAsync(string sql) => SqlServerScript.ExecuteAsync(Fixture.RequireSettings(), sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => SqlServerScript.ScalarAsync(Fixture.RequireSettings(), sql, Ct);
    }

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

            var sql = ExceptionChain.Find<SqlException>(ex);
            Assert.NotNull(sql);
            Assert.Equal(2601, sql!.Number);
            Assert.Contains("UQ_", sql.Message);
        }
    }

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerBulkSaveIntegrationTests(SqlServerFixture fixture) : BulkSaveIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerMixedBatchIntegrationTests(SqlServerFixture fixture) : MixedBatchIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerChildSaveIntegrationTests(SqlServerFixture fixture) : ChildSaveIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerSaveCancellationIntegrationTests(SqlServerFixture fixture) : SaveCancellationIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerRenamedColumnIntegrationTests(SqlServerFixture fixture) : RenamedColumnIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerPatternMatchIntegrationTests(SqlServerFixture fixture) : PatternMatchIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerServerValueIntegrationTests(SqlServerFixture fixture) : ServerValueIntegrationTests(fixture);

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

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerAddColumnIntegrationTests(SqlServerFixture fixture) : AddColumnIntegrationTests(fixture)
    {
        // A column cannot be dropped while an index or a default constraint depends on it.
        protected override string DropColumnsSql(string table, params string[] columns) => string.Concat(columns.Select(c =>
            $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'NCI_{table}_{c}' AND object_id = OBJECT_ID(N'dbo.{table}')) DROP INDEX [NCI_{table}_{c}] ON [dbo].[{table}]; " +
            $"IF OBJECT_ID(N'dbo.DF_{table}_{c}', N'D') IS NOT NULL ALTER TABLE [dbo].[{table}] DROP CONSTRAINT [DF_{table}_{c}]; " +
            $"ALTER TABLE [dbo].[{table}] DROP COLUMN [{c}]; "));

        protected override string NarrowColumnSql(string table, string column, int length) =>
            $"ALTER TABLE [dbo].[{table}] ALTER COLUMN [{column}] varchar({length}) NULL";

        protected override string TableIdSql(string table) => $"SELECT CAST(OBJECT_ID(N'dbo.{table}') AS bigint)";

        protected override string DeleteAllSql(string table) => $"DELETE FROM [dbo].[{table}]";

        protected override Task ExecuteAsync(string sql) => SqlServerScript.ExecuteAsync(Fixture.RequireSettings(), sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => SqlServerScript.ScalarAsync(Fixture.RequireSettings(), sql, Ct);
    }
}
