using System.Linq.Expressions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Baseline behaviour of the LINQ predicate translation (expression -> WHERE clause),
    /// asserted through the rendered SQL so the Phase 2 seam refactor can prove it changed nothing.
    /// Known gaps pinned here as Throws-assertions: IN rendering and instance List.Contains.
    /// </summary>
    public class ExpressionParserTests
    {
        private static SelectQuery NewSelect() =>
            new("[dbo].[tbl_TestWidget]", ["ID", "Name", "Quantity"], typeof(TestWidget));

        private static string ParseToSql(Expression<Func<TestWidget, bool>> predicate) =>
            SqlSnapshot.Normalize(NewSelect().Parse(predicate.Body).BuildQuery());

        [Fact]
        public void Equal_RendersEqualsParameter()
        {
            var sql = ParseToSql(w => w.Name == "abc");

            Assert.Contains("WHERE [Name]=@p0", sql);
        }

        [Fact]
        public void NotEqual_RendersNotEquals()
        {
            var sql = ParseToSql(w => w.Quantity != 5);

            Assert.Contains("[Quantity]<>@p0", sql);
        }

        [Fact]
        public void GreaterThan_RendersOperator()
        {
            var sql = ParseToSql(w => w.Quantity > 3);

            Assert.Contains("[Quantity]>@p0", sql);
        }

        [Fact]
        public void LessThanOrEqual_RendersOperator()
        {
            var sql = ParseToSql(w => w.Quantity <= 9);

            Assert.Contains("[Quantity]<=@p0", sql);
        }

        [Fact]
        public void AndAlso_CombinesBothConditions()
        {
            var sql = ParseToSql(w => w.Name == "abc" && w.Quantity > 3);

            Assert.Contains("[Name]=@p0 AND [Quantity]>@p1", sql);
        }

        [Fact]
        public void OrElse_CombinesWithParenthesisedOr()
        {
            var sql = ParseToSql(w => w.Name == "abc" || w.Name == "def");

            Assert.Contains("([Name]=@p0) OR ([Name]=@p1)", sql);
        }

        [Fact]
        public void StringContains_RendersParameterizedFullTextContains()
        {
            var sql = ParseToSql(w => w.Name!.Contains("bc"));

            Assert.Contains("CONTAINS([Name], @p0)", sql);
        }

        [Fact]
        public void StringStartsWith_RendersLikeWithParameter()
        {
            var sql = ParseToSql(w => w.Name!.StartsWith("ab"));

            Assert.Contains("[Name] LIKE @p0 + '%'", sql);
        }

        [Fact]
        public void ArrayContains_RendersInWithPerElementParameters()
        {
            var names = new[] { "a", "b" };
            var sql = ParseToSql(w => names.Contains(w.Name!));

            Assert.Contains("[Name] IN (@p0_0,@p0_1)", sql);
        }

        [Fact]
        public void ListInstanceContains_RendersInWithPerElementParameters()
        {
            var names = new List<string> { "a", "b" };
            var sql = ParseToSql(w => names.Contains(w.Name!));

            Assert.Contains("[Name] IN (@p0_0,@p0_1)", sql);
        }

        [Fact]
        public void NegatedComparison_InvertsOperator()
        {
            var sql = ParseToSql(w => !(w.Quantity > 3));

            Assert.Contains("[Quantity]<=@p0", sql);
        }

        [Fact]
        public void NegatedStringEquals_RendersNotEquals()
        {
            var sql = ParseToSql(w => !w.Name!.Equals("abc"));

            Assert.Contains("[Name]<>@p0", sql);
        }

        [Fact]
        public void ClosureVariable_IsEvaluatedToValue()
        {
            var target = "closure-value";
            var query = NewSelect();
            Expression<Func<TestWidget, bool>> predicate = w => w.Name == target;

            query.Parse(predicate.Body);
            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            // The closure must be folded to a parameter value, not rendered as a member access.
            Assert.Contains("[Name]=@p0", sql);
            Assert.DoesNotContain("target", sql);
        }
    }
}
