using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// An ordered read places a null below every value on every engine. PostgreSQL's own default
    /// for an index is the other way, and an index made that way serves no ordered read, so its
    /// indexes are created with nulls placed low and the schema check replaces one that is not.
    /// </summary>
    public class IndexNullOrderTests
    {
        private static readonly PostgresSchemaDialect Postgres = new("public", null);

        private static TableSchema Table() => new(ObjectSchema.Get<TestSoftChild>());

        private static List<CatalogIndex> Stored(TableSchema table, bool nullsLow)
        {
            var column = table["Parent"];
            return [new() { TableName = table.TableName, IndexName = IndexNaming.ColumnIndexName(table, column), ColumnName = column.ColumnName, NullsSortLow = nullsLow }];
        }

        [Fact]
        public void APostgresColumnIndex_PlacesNullsLow()
        {
            var table = Table();

            var sql = Postgres.CreateIndex(table, table["Parent"]).Text;

            Assert.Contains("(\"Parent\" ASC NULLS FIRST)", sql);
        }

        [Fact]
        public void APostgresTableIndex_PlacesNullsLow_OnEveryKey()
        {
            var table = new TableSchema(ObjectSchema.Get<TestIndexedWidget>());

            var sql = Postgres.CreateTableIndex(table, table.TableIndexes[0]).Text;

            Assert.DoesNotContain(" ASC,", sql);
            Assert.DoesNotContain(" ASC)", sql);
            Assert.Contains("ASC NULLS FIRST", sql);
        }

        [Fact]
        public void AnIndexThatPlacesNullsHigh_IsReplaced()
        {
            var table = Table();

            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, nullsLow: false), Postgres);

            Assert.Single(drop);
            Assert.Contains(create, c => c.ColumnName == "Parent");
        }

        [Fact]
        public void AnIndexThatPlacesNullsLow_IsCurrent()
        {
            var table = Table();

            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, nullsLow: true), Postgres);

            Assert.Empty(drop);
            Assert.DoesNotContain(create, c => c.ColumnName == "Parent");
        }

        [Fact]
        public void AnEngineThatOnlyOrdersNullsLow_WritesNothingForIt()
        {
            var table = Table();

            Assert.DoesNotContain("NULLS", SqlServerSchemaDialect.ColumnIndexSql(table, table["Parent"]));
        }
    }
}
