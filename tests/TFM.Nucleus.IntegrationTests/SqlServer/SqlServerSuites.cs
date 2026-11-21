using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    // Every provider-neutral suite, run against the SQL Server test database.

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerCrudIntegrationTests(SqlServerFixture fixture) : CrudIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerTransactionCommitIntegrationTests(SqlServerFixture fixture) : TransactionCommitIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerContextSaveIntegrationTests(SqlServerFixture fixture) : ContextSaveIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerManagerLoadIntegrationTests(SqlServerFixture fixture) : ManagerLoadIntegrationTests(fixture);

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

        protected override string IsUniqueSql(string table, string index) =>
            $"SELECT CAST(is_unique AS int) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = N'{index}'";

        protected override string CreatePlainIndexSql(string table, string index, params string[] columns) =>
            $"CREATE NONCLUSTERED INDEX [{index}] ON [dbo].[{table}]({string.Join(", ", columns.Select(c => $"[{c}] ASC"))})";

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
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
    public sealed class SqlServerChildModelIntegrationTests(SqlServerFixture fixture) : ChildModelIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerLinkedCollectionIntegrationTests(SqlServerFixture fixture) : LinkedCollectionIntegrationTests(fixture);

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerLoadHookIntegrationTests(SqlServerFixture fixture) : LoadHookIntegrationTests(fixture);

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
    public sealed class SqlServerReadBenchmarks(SqlServerFixture fixture) : Benchmarks.ReadBenchmarks(fixture);





    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerAddColumnIntegrationTests(SqlServerFixture fixture) : AddColumnIntegrationTests(fixture)
    {
        // A column cannot be dropped while an index or a default constraint depends on it.
        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        protected override string NarrowColumnSql(string table, string column, int length) =>
            $"ALTER TABLE [dbo].[{table}] ALTER COLUMN [{column}] varchar({length}) NULL";

        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }

    [Collection(SqlServerCollection.Name)]
    public sealed class SqlServerTableNamingIntegrationTests(SqlServerFixture fixture) : TableNamingIntegrationTests(fixture)
    {
        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        protected override string DropTableIfExistsSql(string table) => Fixture.Database.DropTableIfExistsSql(table);

        protected override string RenameTableSql(string from, string to) => Fixture.Database.RenameTableSql(from, to);

        protected override string CopyTableSql(string from, string to) => Fixture.Database.CopyTableSql(from, to);

        protected override string RowCountSql(string table) => Fixture.Database.RowCountSql(table);

        // Indexes (the primary key's among them), and the constraints and triggers the table owns.
        // FOR XML PATH because STRING_AGG needs a newer compatibility level than the test database may have.
        protected override string OwnedObjectNamesSql(string table) =>
            $"SELECT STUFF((SELECT ',' + name FROM (" +
            $"SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name IS NOT NULL " +
            $"UNION ALL SELECT name FROM sys.objects WHERE parent_object_id = OBJECT_ID(N'dbo.{table}')) n FOR XML PATH('')), 1, 1, '')";

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }
}
