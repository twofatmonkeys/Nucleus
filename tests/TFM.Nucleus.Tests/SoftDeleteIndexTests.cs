using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// On SQL Server every index of a soft-deletable table carries the soft-delete flag as an
    /// included column, so a read of live rows through the index need not visit each row to test
    /// it. The schema check expects the flag there, and expects it nowhere else: on a table that
    /// is not soft-deletable, and on an engine that was not measured to gain from it.
    /// </summary>
    public class SoftDeleteIndexTests
    {
        private static readonly SqlServerSchemaDialect SqlServer = new(null);

        private static TableSchema Soft() => new(ObjectSchema.Get<TestSoftChild>());
        private static TableSchema Hard() => new(ObjectSchema.Get<TestDeletableChild>());

        private static List<CatalogIndex> Stored(TableSchema table, bool withFlag)
        {
            var column = table["Parent"];
            var name = IndexNaming.ColumnIndexName(table, column);
            List<CatalogIndex> rows = [new() { TableName = table.TableName, IndexName = name, ColumnName = column.ColumnName }];
            if (withFlag)
                rows.Add(new() { TableName = table.TableName, IndexName = name, ColumnName = SoftDelete.IsDeletedColumn, IsIncluded = true });
            return rows;
        }

        [Fact]
        public void AColumnIndexOfASoftDeletableTable_IncludesTheFlag()
        {
            var table = Soft();

            var sql = SqlServerSchemaDialect.ColumnIndexSql(table, table["Parent"]);

            Assert.Contains("([Parent] ASC) INCLUDE ([IsDeleted]) ", sql);
        }

        [Fact]
        public void AColumnIndexOfAnyOtherTable_IncludesNothing()
        {
            var table = Hard();

            var sql = SqlServerSchemaDialect.ColumnIndexSql(table, table["Parent"]);

            Assert.DoesNotContain("INCLUDE", sql);
        }

        [Fact]
        public void AnIndexWithoutTheFlag_IsReplaced_OnASoftDeletableTable()
        {
            var table = Soft();

            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, withFlag: false), SqlServer);

            Assert.Single(drop);
            Assert.Contains(create, c => c.ColumnName == "Parent");
        }

        [Fact]
        public void AnIndexWithTheFlag_IsCurrent_OnASoftDeletableTable()
        {
            var table = Soft();

            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, withFlag: true), SqlServer);

            Assert.Empty(drop);
            Assert.DoesNotContain(create, c => c.ColumnName == "Parent");
        }

        [Fact]
        public void AnIndexWithTheFlag_IsReplaced_OnATableThatIsNotSoftDeletable()
        {
            var table = Hard();

            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, withFlag: true), SqlServer);

            Assert.Single(drop);
            Assert.Contains(create, c => c.ColumnName == "Parent");
        }

        [Fact]
        public void AnEngineNotMeasuredToGain_KeepsItsIndexesAsTheyWere()
        {
            var table = Soft();
            var postgres = new PostgresSchemaDialect("public", null);

            Assert.Empty(SchemaComparer.CarriedColumns(table, postgres));
            var (drop, create) = SchemaComparer.DiffColumnIndexes(table, Stored(table, withFlag: false), postgres);
            Assert.Empty(drop);
            Assert.DoesNotContain(create, c => c.ColumnName == "Parent");
        }
    }
}
