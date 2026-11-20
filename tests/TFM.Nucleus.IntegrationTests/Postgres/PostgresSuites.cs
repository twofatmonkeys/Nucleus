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
    public sealed class PostgresRowQueryIntegrationTests(PostgresFixture fixture) : RowQueryIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresTableIndexIntegrationTests(PostgresFixture fixture) : TableIndexIntegrationTests(fixture)
    {
        protected override string DropIndexSql(string table, string index) => $"DROP INDEX \"public\".\"{index}\"";

        // Positions past indnkeyatts are the INCLUDE columns.
        protected override string IndexColumnsSql(string table, string index) =>
            "SELECT string_agg(CASE WHEN k.n > ix.indnkeyatts THEN '+' ELSE '' END || a.attname, ',' ORDER BY k.n > ix.indnkeyatts, CASE WHEN k.n > ix.indnkeyatts THEN 0 ELSE k.n END, a.attname) " +
            "FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid " +
            "CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, n) " +
            "JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = k.attnum " +
            $"WHERE i.relname = '{index}'";

        protected override string IsUniqueSql(string table, string index) =>
            $"SELECT ix.indisunique::int FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid WHERE i.relname = '{index}'";

        protected override string CreatePlainIndexSql(string table, string index, params string[] columns) =>
            $"CREATE INDEX \"{index}\" ON \"public\".\"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\" ASC"))})";

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }

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
    public sealed class PostgresChildSaveIntegrationTests(PostgresFixture fixture) : ChildSaveIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresChildModelIntegrationTests(PostgresFixture fixture) : ChildModelIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresLinkedCollectionIntegrationTests(PostgresFixture fixture) : LinkedCollectionIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresLoadHookIntegrationTests(PostgresFixture fixture) : LoadHookIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresSaveCancellationIntegrationTests(PostgresFixture fixture) : SaveCancellationIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresRenamedColumnIntegrationTests(PostgresFixture fixture) : RenamedColumnIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresPatternMatchIntegrationTests(PostgresFixture fixture) : PatternMatchIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresServerValueIntegrationTests(PostgresFixture fixture) : ServerValueIntegrationTests(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresSaveBenchmarks(PostgresFixture fixture) : Benchmarks.SaveBenchmarks(fixture);

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresReadBenchmarks(PostgresFixture fixture) : Benchmarks.ReadBenchmarks(fixture);





    [Collection(PostgresCollection.Name)]
    public sealed class PostgresAddColumnIntegrationTests(PostgresFixture fixture) : AddColumnIntegrationTests(fixture)
    {
        // Dropping a column drops its indexes with it. The provider's tables live in public.
        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        protected override string NarrowColumnSql(string table, string column, int length) =>
            $"ALTER TABLE \"public\".\"{table}\" ALTER COLUMN \"{column}\" TYPE varchar({length})";

        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }

    [Collection(PostgresCollection.Name)]
    public sealed class PostgresTableNamingIntegrationTests(PostgresFixture fixture) : TableNamingIntegrationTests(fixture)
    {
        protected override string TableIdSql(string table) => Fixture.Database.TableIdSql(table);

        protected override string DeleteAllSql(string table) => Fixture.Database.DeleteAllSql(table);

        protected override string DropColumnsSql(string table, params string[] columns) => Fixture.Database.DropColumnsSql(table, columns);

        protected override string DropTableIfExistsSql(string table) => Fixture.Database.DropTableIfExistsSql(table);

        protected override string RenameTableSql(string from, string to) => Fixture.Database.RenameTableSql(from, to);

        protected override string CopyTableSql(string from, string to) => Fixture.Database.CopyTableSql(from, to);

        protected override string RowCountSql(string table) => Fixture.Database.RowCountSql(table);

        // Indexes (the primary key's among them), constraints and user triggers of the table.
        protected override string OwnedObjectNamesSql(string table)
        {
            var oid = $"to_regclass('\"public\".\"{table}\"')";
            return $"SELECT string_agg(name, ',') FROM (" +
                   $"SELECT i.relname::text AS name FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid WHERE x.indrelid = {oid} " +
                   $"UNION ALL SELECT conname::text FROM pg_constraint WHERE conrelid = {oid} " +
                   $"UNION ALL SELECT tgname::text FROM pg_trigger WHERE tgrelid = {oid} AND NOT tgisinternal) n";
        }

        protected override Task ExecuteAsync(string sql) => Fixture.Database.ExecuteAsync(sql, Ct);

        protected override Task<object?> ScalarAsync(string sql) => Fixture.Database.ScalarAsync(sql, Ct);
    }
}
