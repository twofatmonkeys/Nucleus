namespace TFM.Nucleus.Tests
{
    /// <summary>A pattern term binds its value with the engines' wildcards escaped, so they match literally.</summary>
    public class LikePatternTests
    {
        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("50%", @"50\%")]
        [InlineData("a_b", @"a\_b")]
        [InlineData("[x]", @"\[x]")]
        [InlineData(@"c:\temp", @"c:\\temp")]
        [InlineData(@"%_[\", @"\%\_\[\\")]
        public void Escape_PrecedesEverySpecialCharacter(string value, string escaped) =>
            Assert.Equal(escaped, LikePattern.Escape(value));

        [Theory]
        [InlineData(Operator.Like, true)]
        [InlineData(Operator.LikeCaseSensitive, true)]
        [InlineData(Operator.StartsWith, true)]
        [InlineData(Operator.EndsWith, true)]
        [InlineData(Operator.ContainsCaseSensitive, true)]
        [InlineData(Operator.Contains, false)]
        [InlineData(Operator.Equals, false)]
        [InlineData(Operator.EqualsIgnoreCase, false)]
        public void Applies_ToThePatternOperatorsOnly(Operator op, bool applies) =>
            Assert.Equal(applies, LikePattern.Applies(op));

        [Fact]
        public void Bind_EscapesAPatternTermsValue()
        {
            var term = new Parameter("Name", "50%_", Operator.StartsWith);

            var bound = Assert.Single(ParameterBinding.Bind(term));

            Assert.Equal(@"50\%\_", bound.Value);
        }

        [Theory]
        [InlineData(Operator.Equals)]
        [InlineData(Operator.Contains)]
        public void Bind_LeavesOtherTermsValuesAsTheyAre(Operator op)
        {
            var term = new Parameter("Name", "50%_", op);

            Assert.Equal("50%_", Assert.Single(ParameterBinding.Bind(term)).Value);
        }
    }
}
