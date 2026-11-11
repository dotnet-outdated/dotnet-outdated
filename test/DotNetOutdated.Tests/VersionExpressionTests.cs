using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class VersionExpressionTests
    {
        [Theory]
        [InlineData("", false)]
        [InlineData("1.0.0", false)]
        [InlineData("$V", false)]
        [InlineData("$(V)", true)]
        [InlineData("[$(V), 2.0.0)", true)]
        public void ContainsPropertyReference(string expression, bool expected)
        {
            Assert.Equal(expected, VersionExpression.ContainsPropertyReference(expression));
        }

        [Theory]
        [InlineData("$(V)", "V")]
        [InlineData("  $(V)  ", "V")]
        [InlineData("$( V )", "V")]
        [InlineData("$(My.Version-2_x)", "My.Version-2_x")]
        [InlineData("$(_V)", "_V")]
        public void TryGetSinglePropertyReference_SingleReference_ReturnsName(string expression, string expectedName)
        {
            Assert.True(VersionExpression.TryGetSinglePropertyReference(expression, out var name));
            Assert.Equal(expectedName, name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1.0.0")]
        [InlineData("$(V)x")]
        [InlineData("x$(V)")]
        [InlineData("$(A)$(B)")]
        [InlineData("$(1V)")]
        [InlineData("[$(V), 2.0.0)")]
        public void TryGetSinglePropertyReference_OtherExpressions_ReturnsFalse(string expression)
        {
            Assert.False(VersionExpression.TryGetSinglePropertyReference(expression, out var name));
            Assert.Equal(string.Empty, name);
        }

        [Theory]
        [InlineData("$(V)", "V", false)]
        [InlineData(" $(V) ", "V", false)]
        [InlineData("[$(V), 2.0.0)", "V", true)]
        [InlineData("[$(V),2.0.0)", "V", true)]
        [InlineData(" ( $( V ) , 2.0.0 ] ", "V", true)]
        [InlineData("[$(V), )", "V", true)]
        public void TryParse_SupportedExpressions(string expression, string expectedName, bool expectedIsRange)
        {
            Assert.True(VersionExpression.TryParse(expression, out var name, out var isRange));
            Assert.Equal(expectedName, name);
            Assert.Equal(expectedIsRange, isRange);
        }

        [Theory]
        [InlineData("")]
        [InlineData("1.0.0")]
        [InlineData("[1.0.0, 2.0.0)")]
        [InlineData("[1.0.0, $(V))")]
        [InlineData("[$(V), $(Upper))")]
        [InlineData("[$(V)-beta, 2.0.0)")]
        [InlineData("$(V)-beta")]
        public void TryParse_UnsupportedExpressions_ReturnsFalse(string expression)
        {
            Assert.False(VersionExpression.TryParse(expression, out var name, out var isRange));
            Assert.Equal(string.Empty, name);
            Assert.False(isRange);
        }

        [Theory]
        [InlineData("$(V)", "1.2.3", "[1.2.3, )")]
        [InlineData("$(V)", "[1.0.0, 2.0.0)", "[1.0.0, 2.0.0)")]
        [InlineData("[$(V), 2.0.0)", "1.2.3", "[1.2.3, 2.0.0)")]
        [InlineData("($(V), )", "1.2.3", "(1.2.3, )")]
        public void TryEvaluate_SupportedExpression_ReturnsRange(string expression, string value, string expected)
        {
            Assert.True(VersionExpression.TryEvaluate(expression, value, out var range));
            Assert.Equal(VersionRange.Parse(expected), range);
        }

        [Theory]
        [InlineData("$(V)", null)]
        [InlineData("$(V)", "not-a-version")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("$(V)-beta", "1.0.0")]
        [InlineData("[1.0.0, $(V))", "2.0.0")]
        public void TryEvaluate_UnsupportedExpressionOrValue_ReturnsFalse(string expression, string? value)
        {
            Assert.False(VersionExpression.TryEvaluate(expression, value, out var range));
            Assert.Null(range);
        }

        [Theory]
        // Not a range over a variable: written back unchanged.
        [InlineData("$(V)", "2.0.0", "$(V)")]
        [InlineData("1.0.0", "2.0.0", "1.0.0")]
        [InlineData("[1.0.0, $(V))", "2.0.0", "[1.0.0, $(V))")]
        // Already evaluates to the requested range: the original text is kept, formatting included.
        [InlineData("[ $(V) ,2.0.0 )", "[1.5.0, 2.0.0)", "[ $(V) ,2.0.0 )")]
        // Otherwise the requested range is written with the variable as its lower bound.
        [InlineData("[$(V), 2.0.0)", "1.5.0", "$(V)")]
        [InlineData("[$(V), 2.0.0)", "[1.5.0, 3.0]", "[$(V), 3.0.0]")]
        [InlineData("[$(V), 2.0.0)", "(1.5.0, 3.0.0)", "($(V), 3.0.0)")]
        [InlineData("[$(V), 2.0.0)", "[1.5.0, 3.0.0)", "[$(V), 3.0.0)")]
        [InlineData("[$(V), 2.0.0)", "(1.5.0, 3.0.0]", "($(V), 3.0.0]")]
        [InlineData("[$(V), 2.0.0)", "(1.5.0, )", "($(V), )")]
        public void Compute(string original, string newRange, string expected)
        {
            Assert.Equal(expected, VersionExpression.Compute(original, "V", VersionRange.Parse(newRange)));
        }

        [Fact]
        public void Compute_UsesTheGivenVariableName()
        {
            Assert.Equal("[$(Other), 3.0.0)", VersionExpression.Compute("[$(V), 2.0.0)", "Other", VersionRange.Parse("[1.5.0, 3.0.0)")));
        }
    }
}
