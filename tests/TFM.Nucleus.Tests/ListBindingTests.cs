using System.Linq.Expressions;
using TFM.Nucleus.Data;
using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.SQL;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A multi-valued term's values are read once into a <see cref="ValueList"/>, and each
    /// renderer says whether it writes the term against one parameter or one per value. The
    /// goldens pin the text; these pin the list and the choice.
    /// </summary>
    public class ListBindingTests
    {
        [Fact]
        public void AValueList_KeepsEachValueOnce_InOrder_WithoutNulls()
        {
            var list = ValueList.From(new int?[] { 3, 1, null, 3, 2, 1 });

            Assert.Equal([3, 1, 2], list.Cast<int>());
            Assert.Equal(typeof(int), list.ElementType);
        }

        [Fact]
        public void AValueList_OfSeveralTypes_OrOfNothing_HasNoElementType()
        {
            Assert.Null(ValueList.From(new object[] { 1, "a" }).ElementType);
            Assert.Null(ValueList.From(Array.Empty<Guid>()).ElementType);
            Assert.Null(ValueList.From(new string?[] { null }).ElementType);
        }

        [Fact]
        public void ATerm_ReadsItsListOnce()
        {
            var walks = 0;
            IEnumerable<int> Values()
            {
                walks++;
                yield return 1;
                yield return 2;
            }

            var term = new Parameter("Quantity", Values(), Operator.In);
            _ = ParameterBinding.Values(term).Count;
            _ = ParameterBinding.Bind(term).ToList();
            _ = SqlServerQueryRenderer.WhereCondition(term);

            Assert.Equal(1, walks);
        }

        [Theory]
        [InlineData(200, false)]
        [InlineData(201, true)]
        public void SqlServer_SendsAListAsOneParameter_OnlyPastTheThreshold(int count, bool asList)
        {
            var term = new Parameter("ID", Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList(), Operator.In);

            Assert.Equal(asList, SqlServerQueryRenderer.Instance.BindsAsList(term));
            Assert.Equal(asList ? 1 : count, ParameterBinding.Count([term], SqlServerQueryRenderer.Instance));
        }

        [Fact]
        public void SqlServer_BindsAListWithNoListType_AValueAtATime()
        {
            var dates = new Parameter("Timestamp", Enumerable.Range(0, 300).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToList(), Operator.In);
            var mixed = new Parameter("Quantity", Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? (object)i : (long)i).ToList(), Operator.In);
            var longText = new Parameter("Name", Enumerable.Range(0, 300).Select(i => new string('x', 4001) + i).ToList(), Operator.In);

            Assert.False(SqlServerQueryRenderer.Instance.BindsAsList(dates));
            Assert.False(SqlServerQueryRenderer.Instance.BindsAsList(mixed));
            Assert.False(SqlServerQueryRenderer.Instance.BindsAsList(longText));
        }

        [Fact]
        public void PostgresAndSqlite_SendAListAsOneParameter_AtAnyLength()
        {
            var one = new Parameter("ID", new List<Guid> { Guid.NewGuid() }, Operator.In);
            var none = new Parameter("ID", new List<Guid>(), Operator.In);

            Assert.True(new PostgresQueryRenderer("public").BindsAsList(one));
            Assert.True(SqliteQueryRenderer.Instance.BindsAsList(one));
            // Nothing to bind: the term is written as one that matches no row.
            Assert.False(new PostgresQueryRenderer("public").BindsAsList(none));
            Assert.False(SqliteQueryRenderer.Instance.BindsAsList(none));
        }

        [Fact]
        public void AnEmptyList_MatchesNothing_AndItsNegationEverything_WithoutAQuery()
        {
            var none = new List<Guid>();
            var nulls = new List<string?> { null };
            Expression<Func<TestWidget, bool>> contains = w => none.Contains(w.ID);
            Expression<Func<TestWidget, bool>> onlyNulls = w => nulls.Contains(w.Name);
            Expression<Func<TestWidget, bool>> besideATerm = w => w.Quantity == 1 && none.Contains(w.ID);
            Expression<Func<TestWidget, bool>> orATerm = w => w.Quantity == 1 || none.Contains(w.ID);
            Expression<Func<TestWidget, bool>> negated = w => !none.Contains(w.ID);
            Expression<Func<TestWidget, bool>> array = w => Array.Empty<Guid>().Contains(w.ID);

            Assert.Equal(PredicateShape.MatchesNone, ExpressionParser.Classify(contains));
            Assert.Equal(PredicateShape.MatchesNone, ExpressionParser.Classify(onlyNulls));
            Assert.Equal(PredicateShape.MatchesNone, ExpressionParser.Classify(besideATerm));
            Assert.Equal(PredicateShape.Filtered, ExpressionParser.Classify(orATerm));
            Assert.Equal(PredicateShape.MatchesAll, ExpressionParser.Classify(negated));
            Assert.Equal(PredicateShape.MatchesNone, ExpressionParser.Classify(array));
            Assert.Throws<ArgumentException>(() => ExpressionParser.CheckDeletePredicate(negated));
        }

        [Fact]
        public void AStringsContains_IsNotAList()
        {
            Expression<Func<TestWidget, bool>> substring = w => w.Name!.Contains("");

            Assert.Equal(PredicateShape.Filtered, ExpressionParser.Classify(substring));
        }
    }
}
