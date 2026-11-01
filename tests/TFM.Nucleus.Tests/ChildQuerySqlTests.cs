namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The SQL of child collection queries. A child selects its rows by the IDs of exactly the
    /// parent rows returned, so a page or a TOP on the parent must reach the child's subquery.
    /// </summary>
    public class ChildQuerySqlTests
    {
        private static (SelectQuery Parent, SelectQuery Child) ParentWithChild(Action<SelectQuery>? configure = null)
        {
            var parent = new SelectQuery(SqlServerQueryRenderer.Instance, new TableSchema(ObjectSchema.Get<TestDeletableWidget>()));
            configure?.Invoke(parent);
            var child = new SelectQuery(SqlServerQueryRenderer.Instance, new TableSchema(ObjectSchema.Get<TestDeletableChild>())) { ChildCollectionProperty = "Children" };
            parent.AddChildQuery(child, "Parent");
            return (parent, child);
        }

        private const string Parent = "tbl_TFM_Nucleus_Tests_TestDeletableWidget";

        private static string ChildSql(SelectQuery child) => SqlSnapshot.Normalize(child.BuildQuery());

        [Fact]
        public void Child_SelectsByEveryMatchingParent()
        {
            var (parent, child) = ParentWithChild(p => p.Where.And("Name", "a"));

            Assert.EndsWith($"WHERE [Parent] IN (SELECT [ID] FROM {Parent} WHERE [Name]=@p0)", ChildSql(child));
        }

        [Fact]
        public void Child_OfTopParent_SelectsTheSameTopParentsInTheSameOrder()
        {
            var (parent, child) = ParentWithChild(p => p.Take = 5);

            Assert.EndsWith($"WHERE [Parent] IN (SELECT TOP 5 [ID] FROM {Parent} ORDER BY [ID])", ChildSql(child));
            Assert.EndsWith("ORDER BY [ID]", SqlSnapshot.Normalize(parent.BuildQuery(includeChildQueries: false)));
        }

        [Fact]
        public void Child_OfPagedParent_SelectsOnlyThePage()
        {
            var (_, child) = ParentWithChild(p => { p.Skip = 10; p.Take = 5; });

            Assert.EndsWith(
                $"WHERE [Parent] IN (SELECT [ID] FROM (SELECT [ID], ROW_NUMBER() OVER (ORDER BY [ID]) AS [__RowNumber] FROM {Parent}) AS [__paged] " +
                "WHERE [__paged].[__RowNumber] > 10 AND [__paged].[__RowNumber] <= 15)",
                ChildSql(child));
        }

        [Fact]
        public void BuildQuery_WithoutChildQueries_LeavesThemOut()
        {
            var (parent, _) = ParentWithChild();

            Assert.DoesNotContain("TestDeletableChild", parent.BuildQuery(includeChildQueries: false));
            Assert.Contains("TestDeletableChild", parent.BuildQuery());
        }

        [Fact]
        public void Child_OfSoftParentIncludingDeleted_IncludesDeletedChildren()
        {
            var parent = new SelectQuery(SqlServerQueryRenderer.Instance, new TableSchema(ObjectSchema.Get<TestSoftWidget>())) { IncludeDeleted = true };
            var child = new SelectQuery(SqlServerQueryRenderer.Instance, new TableSchema(ObjectSchema.Get<TestSoftWidget>()));
            parent.AddChildQuery(child, "Parent");

            Assert.DoesNotContain("[IsDeleted]", child.BuildQuery());
        }
    }
}
