namespace TFM.Nucleus.Tests
{
    /// <summary>How the SQL Server provider reads and compares its catalog.</summary>
    public class SqlServerCatalogTests
    {
        private static ColumnSchema NameColumn() =>
            new TableSchema(ObjectSchema.Get<TestWidget>()).FindByPropertyName("Name")!;

        [Fact]
        public void Matches_TheSameType_IgnoringTheCatalogsCase()
        {
            var column = NameColumn();

            Assert.True(SqlServerTypeMapper.Matches(column, new CatalogColumn { TableName = "t", Name = "Name", TypeName = "varchar", DataLength = column.DataLength!.Value }));
        }

        [Fact]
        public void Matches_ATypeNucleusDoesNotKnow_IsAMismatchNotAnError()
        {
            Assert.False(SqlServerTypeMapper.Matches(NameColumn(), new CatalogColumn { TableName = "t", Name = "Name", TypeName = "sql_variant" }));
        }

        [Fact]
        public void ColumnsQuery_ForOneTable_BindsTheName()
        {
            var statement = SqlServerCatalogReader.ColumnsQuery("tbl_x'; DROP TABLE y; --");

            Assert.DoesNotContain("tbl_x", statement.Text);
            Assert.Contains("@tableName", statement.Text);
            var parameter = Assert.Single(statement.Parameters!);
            Assert.Equal("tableName", parameter!.Variable);
            Assert.Equal("tbl_x'; DROP TABLE y; --", parameter.Value);
        }

        [Fact]
        public void ColumnsQuery_ForEveryTable_HasNoFilter()
        {
            var statement = SqlServerCatalogReader.ColumnsQuery(null);

            Assert.DoesNotContain("WHERE", statement.Text);
            Assert.Null(statement.Parameters);
        }
    }
}
