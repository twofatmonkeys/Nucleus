using Microsoft.Data.Sqlite;

namespace TFM.Nucleus.IntegrationTests.Sqlite
{
    // Every provider-neutral suite, run against the SQLite test database file.

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteCrudIntegrationTests(SqliteFixture fixture) : CrudIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteTransactionCommitIntegrationTests(SqliteFixture fixture) : TransactionCommitIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteContextSaveIntegrationTests(SqliteFixture fixture) : ContextSaveIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteManagerLoadIntegrationTests(SqliteFixture fixture) : ManagerLoadIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteChildCollectionLoadIntegrationTests(SqliteFixture fixture) : ChildCollectionLoadIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteDeleteWhereIntegrationTests(SqliteFixture fixture) : DeleteWhereIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteEnumWhereIntegrationTests(SqliteFixture fixture) : EnumWhereIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteRowQueryIntegrationTests(SqliteFixture fixture) : RowQueryIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteTableIndexIntegrationTests(SqliteFixture fixture) : TableIndexIntegrationTests(fixture)
    {
        protected override string DropIndexSql(string table, string index) => $"DROP INDEX \"{index}\"";

        // SQLite's indexes carry no included columns, so the declared index is its keys alone.
        protected override string ExpectedIndexColumns => "Batch,At";

        protected override string IndexColumnsSql(string table, string index) =>
            $"SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_xinfo('{index}') WHERE key = 1 ORDER BY seqno)";

        protected override string IsUniqueSql(string table, string index) =>
            $"SELECT \"unique\" FROM pragma_index_list('{table}') WHERE name = '{index}'";

        protected override string CreatePlainIndexSql(string table, string index, params string[] columns) =>
            $"CREATE INDEX \"{index}\" ON \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\" ASC"))})";

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteExcludeIntegrationTests(SqliteFixture fixture) : ExcludeIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteUniqueIndexIntegrationTests(SqliteFixture fixture) : UniqueIndexIntegrationTests(fixture)
    {
        private readonly SqliteFixture _fixture = fixture;

        /// <summary>The engine's own error is kept as the inner exception: SQLITE_CONSTRAINT_UNIQUE, naming the column.</summary>
        [Fact]
        public async Task DuplicateValue_KeepsTheSqliteErrorInside()
        {
            var manager = _fixture.RequireManager();
            var code = $"uq-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));

            var sqlite = ExceptionChain.Find<SqliteException>(ex);
            Assert.NotNull(sqlite);
            Assert.Equal(19, sqlite!.SqliteErrorCode);
            Assert.Equal(2067, sqlite.SqliteExtendedErrorCode);
            Assert.Contains("tbl_TFM_Nucleus_IntegrationTests_IntUniqueItem.Code", sqlite.Message);
        }
    }

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteBulkSaveIntegrationTests(SqliteFixture fixture) : BulkSaveIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteMixedBatchIntegrationTests(SqliteFixture fixture) : MixedBatchIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteChildSaveIntegrationTests(SqliteFixture fixture) : ChildSaveIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteChildModelIntegrationTests(SqliteFixture fixture) : ChildModelIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteLinkedCollectionIntegrationTests(SqliteFixture fixture) : LinkedCollectionIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteLoadHookIntegrationTests(SqliteFixture fixture) : LoadHookIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteSaveCancellationIntegrationTests(SqliteFixture fixture) : SaveCancellationIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteRenamedColumnIntegrationTests(SqliteFixture fixture) : RenamedColumnIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqlitePatternMatchIntegrationTests(SqliteFixture fixture) : PatternMatchIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteServerValueIntegrationTests(SqliteFixture fixture) : ServerValueIntegrationTests(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteSaveBenchmarks(SqliteFixture fixture) : Benchmarks.SaveBenchmarks(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteReadBenchmarks(SqliteFixture fixture) : Benchmarks.ReadBenchmarks(fixture);

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteAddColumnIntegrationTests(SqliteFixture fixture) : AddColumnIntegrationTests(fixture)
    {
        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        // SQLite cannot change a column's type, so the column is replaced by one declared narrower,
        // keeping its values: renamed aside, added again, copied back, and the old one dropped.
        protected override string NarrowColumnSql(string table, string column, int length) =>
            $"ALTER TABLE \"{table}\" RENAME COLUMN \"{column}\" TO \"{column}_wide\"; " +
            $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" VARCHAR({length}) NULL; " +
            $"UPDATE \"{table}\" SET \"{column}\" = \"{column}_wide\"; " +
            $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}_wide\"";

        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }

    [Collection(SqliteCollection.Name)]
    public sealed class SqliteTableNamingIntegrationTests(SqliteFixture fixture) : TableNamingIntegrationTests(fixture)
    {
        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        protected override string DropTableIfExistsSql(string table) => Fixture.Database.DropTableIfExistsSql(table);

        protected override string RenameTableSql(string from, string to) => Fixture.Database.RenameTableSql(from, to);

        protected override string CopyTableSql(string from, string to) => Fixture.Database.CopyTableSql(from, to);

        protected override string RowCountSql(string table) => Fixture.Database.RowCountSql(table);

        // The table's indexes and triggers. The primary key is the index SQLite makes for it and
        // names after the table (sqlite_autoindex_<table>_1), so it is reported as PK_<table>, the
        // name the other engines give it; SQLite renames that index with its table.
        protected override string OwnedObjectNamesSql(string table)
        {
            var literal = table.Replace("'", "''");
            return "SELECT group_concat(CASE WHEN name = 'sqlite_autoindex_' || tbl_name || '_1' THEN 'PK_' || tbl_name ELSE name END, ',') " +
                   $"FROM sqlite_schema WHERE tbl_name = '{literal}' AND type IN ('index', 'trigger')";
        }

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }
}
