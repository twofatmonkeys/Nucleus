using TFM.Nucleus.Data;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A context numbers the terms of each command before it builds the text, so two commands of
    /// one shape have one text and an engine can reuse the plan it made for the first. Named by a
    /// process-wide counter, as they were, no two commands were ever alike.
    /// </summary>
    public class ParameterNamingTests
    {
        private static string Text(params Parameter[] terms)
        {
            ParameterBinding.NameForCommand(terms);
            return string.Join(" AND ", terms.Select(SqlServerQueryRenderer.WhereCondition));
        }

        [Fact]
        public void TwoCommandsOfOneShape_HaveOneText()
        {
            var first = Text(new Parameter("Name", "a"), new Parameter("Quantity", 1, Operator.GreaterThan));
            var second = Text(new Parameter("Name", "b"), new Parameter("Quantity", 2, Operator.GreaterThan));

            Assert.Equal("[Name]=@p0 AND [Quantity]>@p1", first);
            Assert.Equal(first, second);
        }

        [Fact]
        public void ATermTwoStatementsShare_IsNumberedOnce()
        {
            var shared = new Parameter("Name", "a");
            var other = new Parameter("Quantity", 1);

            ParameterBinding.NameForCommand([shared, other, shared]);

            Assert.Equal("p0", shared.Variable);
            Assert.Equal("p1", other.Variable);
        }

        [Fact]
        public void ATermIsNumberedAgain_ForItsNextCommand()
        {
            var term = new Parameter("Name", "a");
            ParameterBinding.NameForCommand([new Parameter("Quantity", 1), term]);
            Assert.Equal("p1", term.Variable);

            ParameterBinding.NameForCommand([term]);

            Assert.Equal("p0", term.Variable);
        }

        [Fact]
        public void ATermBoundUnderItsColumnsName_KeepsIt()
        {
            var fixedName = new Parameter("tableName", "t", random: false);
            var numbered = new Parameter("Name", "a");

            ParameterBinding.NameForCommand([fixedName, numbered]);

            Assert.Equal("tableName", fixedName.Variable);
            Assert.Equal("p0", numbered.Variable);
        }

        [Fact]
        public void ATermNoCommandNumbered_NamesItself_UniquelyAndOnce()
        {
            var a = new Parameter("Name", "a");
            var b = new Parameter("Name", "b");

            Assert.NotEqual(a.Variable, b.Variable);
            Assert.Equal(a.Variable, a.Variable);
        }

        [Fact]
        public void TheValuesOfAList_AreNamedAfterTheirTerm()
        {
            var list = new Parameter("Quantity", new List<int> { 1, 2 }, Operator.In);

            ParameterBinding.NameForCommand([new Parameter("Name", "a"), list]);

            Assert.Equal(["p1_0", "p1_1"], ParameterBinding.Bind(list).Select(b => b.Key));
            Assert.Equal("[Quantity] IN (@p1_0,@p1_1)", SqlServerQueryRenderer.WhereCondition(list));
        }
    }
}
