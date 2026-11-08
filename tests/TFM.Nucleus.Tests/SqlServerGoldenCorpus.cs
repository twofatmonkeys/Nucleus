namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Every shape of SQL the SQL Server provider renders: the shared query cases through its
    /// renderer, plus its schema scripts, including every store entity's CREATE TABLE.
    /// <see cref="SqlServerRawGoldenTests"/> compares each case with <see cref="SqlServerGoldens"/>
    /// after <see cref="SqlSnapshot.NormalizeVariablesOnly"/>, so a change of a single space or
    /// separator fails.
    /// </summary>
    public static class SqlServerGoldenCorpus
    {
        public static IReadOnlyDictionary<string, Func<string>> Cases { get; } = BuildCases();

        private static Dictionary<string, Func<string>> BuildCases()
        {
            var cases = new QueryGoldenCorpus(SqlServerQueryRenderer.Instance).Build();

            cases["ddl.rename"] = () => string.Join("\n--\n",
                SqlServerSchemaDialect.RenameTableSql("tbl_X", "tbl_X_20260101000000"),
                SqlServerSchemaDialect.RenameDefaultConstraintsSql("p1"),
                SqlServerSchemaDialect.RenameIndexesSql);
            cases["ddl.copy"] = () => string.Join("\n--\n",
                SqlServerSchemaDialect.CopyTableDataSql("tbl_X_20260101000000", "tbl_X", ["ID", "Name", "Timestamp"], orderByClusterId: true),
                SqlServerSchemaDialect.CopyTableDataSql("tbl_X_20260101000000", "tbl_X", ["ID", "Name"], orderByClusterId: false));
            // Columns named with keywords are created bracketed, so the copy must bracket them too.
            cases["ddl.copy.keywords"] = () =>
                SqlServerSchemaDialect.CopyTableDataSql("tbl_X_20260101000000", "tbl_X", ["ID", "By", "Order", "Key", "Timestamp"], orderByClusterId: true);
            cases["ddl.drop"] = () => SqlServerSchemaDialect.DropTableSql("tbl_X_20260101000000");
            cases["ddl.backfill"] = () => SqlServerSchemaDialect.BackfillTimestampSql("tbl_X");
            cases["ddl.dropindex"] = () => SqlServerSchemaDialect.DropColumnIndexSql("NCI_tbl_X_Name", "tbl_X");
            cases["ddl.fulltext"] = () => string.Join("\n--\n",
                SqlServerSchemaDialect.DropFullTextCatalogSql("ftc_tbl_X"),
                SqlServerSchemaDialect.DropFullTextIndexSql("tbl_X"),
                SqlServerSchemaDialect.CreateFullTextIndexSql("tbl_X"),
                SqlServerSchemaDialect.AddFullTextColumnSql("tbl_X", "Searchable"));

            foreach (var type in QueryGoldenCorpus.SchemaTypes())
                cases[$"ddl.create.{type.Name}"] = () =>
                    string.Join("\n--\n", SqlServerSchemaDialect.CreateTableStatements(QueryGoldenCorpus.TableOf(type)));

            return cases;
        }
    }
}
