using TFM.Nucleus.Data.PostgreSQL;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Every shape of SQL the PostgreSQL provider renders: the shared query cases through its
    /// renderer, plus its schema scripts, including every store entity's CREATE TABLE with the
    /// names longer than 63 bytes shortened. <see cref="PostgresRawGoldenTests"/> compares each
    /// case with <see cref="PostgresGoldens"/>.
    /// </summary>
    public static class PostgresGoldenCorpus
    {
        private const string Schema = "public";

        public static IReadOnlyDictionary<string, Func<string>> Cases { get; } = BuildCases();

        private static Dictionary<string, Func<string>> BuildCases()
        {
            var cases = new QueryGoldenCorpus(new PostgresQueryRenderer(Schema)).Build();
            var dialect = new PostgresSchemaDialect(Schema, null);
            static string Join(IEnumerable<TFM.Nucleus.Data.Schema.SchemaStatement> statements) => string.Join("\n--\n", statements.Select(s => s.Text));

            var widget = QueryGoldenCorpus.TableOf(typeof(TestWidget));
            var unique = QueryGoldenCorpus.TableOf(typeof(TestUniqueWidget));
            var allTypes = QueryGoldenCorpus.TableOf(typeof(TestAllTypesWidget));

            cases["ddl.rename"] = () => Join(dialect.RenameForRebuild(widget, "tbl_TFM_Nucleus_Tests_TestWidget_20260101000000"));
            // A table renamed from an old name: its trigger dropped, its key and indexes renamed,
            // column and [TableIndex] indexes, full-text ones, and names shortened past 63 bytes.
            var indexedForAdopt = QueryGoldenCorpus.TableOf(typeof(TestIndexedWidget));
            var touch = QueryGoldenCorpus.TableOf(typeof(TestLongNamedTouchRecordForIdentifiers));
            cases["ddl.adopt"] = () => Join(dialect.AdoptTable("tbl_OldUniqueWidget", unique));
            cases["ddl.adopt.tableindex"] = () => Join(dialect.AdoptTable("tbl_OldIndexedWidget", indexedForAdopt));
            cases["ddl.adopt.fulltext"] = () => Join(dialect.AdoptTable("tbl_OldAllTypesWidget", allTypes));
            cases["ddl.adopt.long"] = () => Join(dialect.AdoptTable("tbl_LongNamedTouchRecord", touch));
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
            var indexed = QueryGoldenCorpus.TableOf(typeof(TestIndexedWidget));
            cases["ddl.tableindex"] = () => dialect.CreateTableIndex(indexed, indexed.TableIndexes[0]).Text;
            var uniqueIndexed = QueryGoldenCorpus.TableOf(typeof(TestUniqueIndexedWidget));
            cases["ddl.tableindex.unique"] = () => dialect.CreateTableIndex(uniqueIndexed, uniqueIndexed.TableIndexes.Single(i => i.Name == "CodeQuantity")).Text;
            cases["ddl.tableindex.unique.include"] = () => dialect.CreateTableIndex(uniqueIndexed, uniqueIndexed.TableIndexes.Single(i => i.Name == "NameCode")).Text;
            cases["ddl.fulltext"] = () => dialect.CreateFullTextIndex(allTypes, allTypes["Searchable"]);
            cases["ddl.notify"] = () => string.Join("\n--\n", dialect.CreateNotifyFunction(), dialect.CreateNotifyTrigger(widget));

            // Foreign keys: added checked or not, with a delete action, dropped, and the orphan count.
            var keeperKey = QueryGoldenCorpus.KeyOf(typeof(FkResident), "Keeper");
            cases["ddl.fk.add"] = () => dialect.AddForeignKey(keeperKey, checkExisting: true).Text;
            cases["ddl.fk.add.nocheck"] = () => dialect.AddForeignKey(keeperKey, checkExisting: false).Text;
            cases["ddl.fk.add.setnull"] = () => dialect.AddForeignKey(QueryGoldenCorpus.KeyOf(typeof(FkResident), "Backup"), checkExisting: true).Text;
            cases["ddl.fk.drop"] = () => dialect.DropForeignKey(keeperKey.Table.TableName, keeperKey.Name).Text;
            cases["ddl.fk.orphans"] = () => dialect.CountOrphans(keeperKey).Text;

            foreach (var type in QueryGoldenCorpus.SchemaTypes())
                cases[$"ddl.create.{type.Name}"] = () => Join(dialect.CreateTable(QueryGoldenCorpus.TableOf(type)));

            return cases;
        }
    }
}
