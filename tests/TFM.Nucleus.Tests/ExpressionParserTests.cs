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

        // NullableWidget carries the enum and nullable-enum columns. The constraint list must name
        // every column a predicate touches, or SelectQuery rejects the term.
        private static SelectQuery NewNullableSelect() =>
            new("[dbo].[tbl_NullableWidget]",
                ["ID", "RestrictedName", "Kind", "OptionalKind", "OptionalQuantity", "OptionalPrice"],
                typeof(NullableWidget));

        /// <summary>Parses a NullableWidget predicate and returns both the SQL and the bound terms.</summary>
        private static (string Sql, ParameterCollection Parameters) ParseNullable(
            Expression<Func<NullableWidget, bool>> predicate)
        {
            var query = NewNullableSelect();
            query.Parse(predicate.Body);
            return (SqlSnapshot.Normalize(query.BuildQuery()), query.Where.AllParameters());
        }

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
        // ---- Enum columns ---------------------------------------------------------------------
        // C# lowers an enum comparison to Convert(w.Kind, Int32) == Constant(int), so the column is
        // not the top node of either side. These pin that the conversion is unwrapped and the value
        // is bound as the underlying integer the column actually stores.

        [Fact]
        public void EnumEquals_UnwrapsConvert()
        {
            var (sql, parameters) = ParseNullable(w => w.Kind == WidgetKind.Premium);

            Assert.Contains("[Kind]=@p0", sql);
            Assert.Equal(2, Assert.Single(parameters)!.Value);
        }

        [Fact]
        public void EnumNotEqual_RendersNotEquals()
        {
            var (sql, _) = ParseNullable(w => w.Kind != WidgetKind.Premium);

            Assert.Contains("[Kind]<>@p0", sql);
        }

        [Fact]
        public void EnumClosureVariable_IsEvaluated()
        {
            var target = WidgetKind.Premium;

            var (sql, parameters) = ParseNullable(w => w.Kind == target);

            Assert.Contains("[Kind]=@p0", sql);
            Assert.DoesNotContain("target", sql);
            Assert.Equal(2, Assert.Single(parameters)!.Value);
        }

        [Fact]
        public void NullableEnumEquals_UnwrapsConvert()
        {
            var (sql, parameters) = ParseNullable(w => w.OptionalKind == WidgetKind.Premium);

            Assert.Contains("[OptionalKind]=@p0", sql);
            Assert.Equal(2, Assert.Single(parameters)!.Value);
        }

        [Fact]
        public void NullableEnumIsNull_RendersIsNull()
        {
            var (sql, _) = ParseNullable(w => w.OptionalKind == null);

            Assert.Contains("[OptionalKind] IS NULL", sql);
        }

        [Fact]
        public void EnumArrayContains_RendersIn()
        {
            var kinds = new[] { WidgetKind.Standard, WidgetKind.Premium };

            var (sql, parameters) = ParseNullable(w => kinds.Contains(w.Kind));

            Assert.Contains("[Kind] IN (@p0_0,@p0_1)", sql);
            var values = Assert.IsAssignableFrom<System.Collections.IEnumerable>(Assert.Single(parameters)!.Value);
            Assert.Equal(new object?[] { 1, 2 }, values.Cast<object?>());
        }

        [Fact]
        public void EnumInAndAlso_RendersBothTerms()
        {
            var (sql, _) = ParseNullable(w => w.RestrictedName == "abc" && w.Kind == WidgetKind.Premium);

            Assert.Contains("[RestrictedName]=@p0 AND [Kind]=@p1", sql);
        }
    }
}
