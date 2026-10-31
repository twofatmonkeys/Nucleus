namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Golden snapshots of the SQL text generated today. These pin the current renderer output
    /// so the Phase 2 provider-seam refactor can prove it did not change any generated SQL.
    /// Update deliberately, never casually.
    /// </summary>
    public class GoldenSqlTests
    {
        [Fact]
        public void Select_NoWhere_Golden()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", ["ID", "Name", "Quantity"], typeof(TestWidget));

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("SELECT [ID] ,[Name] ,[Quantity] FROM [dbo].[tbl_TestWidget] WITH(NOLOCK)", sql);
        }

        [Fact]
        public void Select_NoColumns_Golden()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", typeof(TestWidget));

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("SELECT TOP 0 NULL", sql);
        }

        [Fact]
        public void Select_WithWhere_Golden()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", ["ID", "Name"], typeof(TestWidget));
            query.Where.And("Name", "abc");

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("SELECT [ID] ,[Name] FROM [dbo].[tbl_TestWidget] WITH(NOLOCK) WHERE [Name]=@p0", sql);
        }

        [Fact]
        public void Select_OffsetPagingWithTotalCount_Golden()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", ["ID", "Name"], typeof(TestWidget))
            {
                Skip = 10,
                Take = 5,
                WithTotalCount = true,
            };
            query.Where.And("Name", "abc");

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal(
                "SELECT [ID] ,[Name] FROM (SELECT [ID] ,[Name], ROW_NUMBER() OVER (ORDER BY [ID]) AS [__RowNumber] FROM [dbo].[tbl_TestWidget] WITH(NOLOCK) WHERE [Name]=@p0) AS [__paged] WHERE [__paged].[__RowNumber] > 10 AND [__paged].[__RowNumber] <= 15 ORDER BY [__paged].[__RowNumber]; SELECT COUNT_BIG(*) FROM [dbo].[tbl_TestWidget] WITH(NOLOCK) WHERE [Name]=@p0",
                sql);
        }

        [Fact]
        public void Insert_Golden()
        {
            var query = new InsertQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]");
            query.SetParameters.Add(new Parameter("Name", "Widget A"));
            query.SetParameters.Add(new Parameter("Quantity", 5));

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("INSERT INTO [dbo].[tbl_TestWidget]([Name],[Quantity]) VALUES (@p0,@p1)", sql);
        }

        [Fact]
        public void Update_Golden()
        {
            var id = Guid.NewGuid();
            var query = new UpdateQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", "ID", id);
            query.SetParameters.Add(new Parameter("Name", "Widget B"));

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("UPDATE [dbo].[tbl_TestWidget] WITH (ROWLOCK) SET [Name] = @p0 WHERE [ID]=@p1", sql);
        }
    }
}
