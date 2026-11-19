using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Every shape of SQL the SQLite provider renders: the shared query cases through its
    /// renderer, plus its schema scripts, including every store entity's CREATE TABLE.
    /// <see cref="SqliteRawGoldenTests"/> compares each case with <see cref="SqliteGoldens"/>.
    /// SQLite has no full-text index or change notification objects, so there are no such cases.
    /// </summary>
    public static class SqliteGoldenCorpus
    {
        public static IReadOnlyDictionary<string, Func<string>> Cases { get; } = BuildCases();

        private static Dictionary<string, Func<string>> BuildCases()
        {
            var cases = new QueryGoldenCorpus(SqliteQueryRenderer.Instance).Build();
            var dialect = new SqliteSchemaDialect();
            static string Join(IEnumerable<TFM.Nucleus.Data.Schema.SchemaStatement> statements) => string.Join("\n--\n", statements.Select(s => s.Text));

            var widget = QueryGoldenCorpus.TableOf(typeof(TestWidget));
            var unique = QueryGoldenCorpus.TableOf(typeof(TestUniqueWidget));
            var indexed = QueryGoldenCorpus.TableOf(typeof(TestIndexedWidget));

            // A rebuild drops the indexes the new table will create, column and [TableIndex] ones.
            cases["ddl.rename"] = () => Join(dialect.RenameForRebuild(unique, "tbl_TFM_Nucleus_Tests_TestUniqueWidget_20260101000000"));
            cases["ddl.rename.tableindex"] = () => Join(dialect.RenameForRebuild(indexed, "tbl_TFM_Nucleus_Tests_TestIndexedWidget_20260101000000"));
            cases["ddl.adopt"] = () => Join(dialect.AdoptTable("tbl_OldUniqueWidget", unique));
            cases["ddl.copy"] = () => string.Join("\n--\n",
                dialect.CopyRows("tbl_X_20260101000000", "tbl_X", ["ID", "Name", "Timestamp"], orderByClusterId: true).Text,
                dialect.CopyRows("tbl_X_20260101000000", "tbl_X", ["ID", "Name"], orderByClusterId: false).Text);
            var growing = QueryGoldenCorpus.TableOf(typeof(TestGrowingWidget));
            var added = new[] { growing["Key"], growing["Notes"], growing["Price"], growing["Extra"] };
            cases["ddl.addcolumns"] = () => Join(dialect.AddColumns(growing, added, [])!);
            cases["ddl.addcolumns.softdelete"] = () => Join(dialect.AddColumns(growing, [], [SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn])!);
            cases["ddl.addcolumns.both"] = () => Join(dialect.AddColumns(growing, [growing["Key"]], [SoftDelete.DeletedAtColumn])!);
            cases["ddl.drop"] = () => dialect.DropTable("tbl_X_20260101000000").Text;
            cases["ddl.backfill"] = () => dialect.BackfillTimestamp(widget).Text;
            cases["ddl.index"] = () => string.Join("\n--\n", unique.IndexedColumns.Select(c => dialect.CreateIndex(unique, c).Text));
            cases["ddl.dropindex"] = () => dialect.DropIndex(widget, "NCI_tbl_X_Name").Text;
            cases["ddl.tableindex"] = () => dialect.CreateTableIndex(indexed, indexed.TableIndexes[0]).Text;
            var uniqueIndexed = QueryGoldenCorpus.TableOf(typeof(TestUniqueIndexedWidget));
            cases["ddl.tableindex.unique"] = () => dialect.CreateTableIndex(uniqueIndexed, uniqueIndexed.TableIndexes.Single(i => i.Name == "CodeQuantity")).Text;
            cases["ddl.tableindex.unique.include"] = () => dialect.CreateTableIndex(uniqueIndexed, uniqueIndexed.TableIndexes.Single(i => i.Name == "NameCode")).Text;

            foreach (var type in QueryGoldenCorpus.SchemaTypes())
                cases[$"ddl.create.{type.Name}"] = () => Join(dialect.CreateTable(QueryGoldenCorpus.TableOf(type)));

            return cases;
        }
    }
}
