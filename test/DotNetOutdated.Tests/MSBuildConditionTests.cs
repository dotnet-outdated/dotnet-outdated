#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DotNetOutdated.Core.Services;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class MSBuildConditionTests
    {
        private static readonly Dictionary<string, string> Properties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = "Debug",
            ["A"] = "1",
            ["B"] = "2",
            ["Empty"] = "",
            ["Dir"] = "src/",
        };

        // Expands $(Name) from Properties; an undefined property can't be expanded statically.
        private static string? Expand(string text)
        {
            var failed = false;
            var result = Regex.Replace(text, @"\$\((\w+)\)", match =>
            {
                if (Properties.TryGetValue(match.Groups[1].Value, out var value))
                {
                    return value;
                }

                failed = true;
                return string.Empty;
            });

            return failed ? null : result;
        }

        private static bool? Exists(string path) => path switch
        {
            "present.props" => true,
            "src/" => true,
            "unknowable" => null,
            _ => false,
        };

        private static bool? Evaluate(string condition) => MSBuildCondition.Evaluate(condition, Expand, Exists);

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void EmptyCondition_IsTrue(string condition)
        {
            Assert.True(Evaluate(condition));
        }

        [Theory]
        [InlineData("'$(Configuration)' == 'Debug'", true)]
        [InlineData("'$(Configuration)' == 'Release'", false)]
        [InlineData("'$(Configuration)'=='Debug'", true)]
        [InlineData("'$(Configuration)' == 'DEBUG'", true)]
        [InlineData("'$(Configuration)' != 'Debug'", false)]
        [InlineData("'$(Configuration)' != 'Release'", true)]
        [InlineData("'$(Configuration)'!='Release'", true)]
        [InlineData("'$(Configuration)' != 'debug'", false)]
        [InlineData("'$(Empty)' == ''", true)]
        [InlineData("'$(Empty)' != ''", false)]
        [InlineData("'$(A).$(B)' == '1.2'", true)]
        [InlineData("'' == ''", true)]
        public void StringComparisons(string condition, bool expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        // Unquoted operands, including property references whose parentheses are part of the word.
        [InlineData("$(Configuration) == Debug", true)]
        [InlineData("$(Configuration)==Debug", true)]
        [InlineData("$(Configuration) != Debug", false)]
        [InlineData("a=='a'", true)]
        [InlineData("a!='a'", false)]
        [InlineData("$(A)-$(B) == '1-2'", true)]
        [InlineData("$(A)x == '1x'", true)]
        [InlineData("x$(A) == 'x1'", true)]
        [InlineData("'1' == $(A)", true)]
        [InlineData("'x' == x", true)]
        [InlineData("'x$' == x$", true)]
        [InlineData("'@' == @", true)]
        public void UnquotedOperands(string condition, bool expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("on", true)]
        [InlineData("Yes", true)]
        [InlineData("false", false)]
        [InlineData("False", false)]
        [InlineData("OFF", false)]
        [InlineData("no", false)]
        [InlineData("'true'", true)]
        [InlineData("'no'", false)]
        [InlineData("maybe", null)]
        [InlineData("'1'", null)]
        public void BooleanLiterals(string condition, bool? expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        [InlineData("!true", false)]
        [InlineData("!false", true)]
        [InlineData("! false", true)]
        [InlineData("!!true", true)]
        [InlineData("!maybe", null)]
        [InlineData("!('$(A)' == '1')", false)]
        [InlineData("!Exists('present.props')", false)]
        [InlineData("!Exists('missing.props')", true)]
        public void Not(string condition, bool? expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        // "maybe" is a value that can't be evaluated statically, so it stands for unknown.
        [InlineData("true and true", true)]
        [InlineData("true and false", false)]
        [InlineData("false and true", false)]
        [InlineData("false and false", false)]
        [InlineData("false and maybe", false)]
        [InlineData("maybe and false", false)]
        [InlineData("true and maybe", null)]
        [InlineData("maybe and true", null)]
        [InlineData("maybe and maybe", null)]
        [InlineData("true or true", true)]
        [InlineData("true or false", true)]
        [InlineData("false or true", true)]
        [InlineData("false or false", false)]
        [InlineData("true or maybe", true)]
        [InlineData("maybe or true", true)]
        [InlineData("false or maybe", null)]
        [InlineData("maybe or false", null)]
        [InlineData("maybe or maybe", null)]
        [InlineData("true AND true", true)]
        [InlineData("false OR true", true)]
        [InlineData("true and true and false", false)]
        [InlineData("false or false or true", true)]
        public void AndOr_ThreeValuedLogic(string condition, bool? expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        // "and" binds tighter than "or".
        [InlineData("true or false and false", true)]
        [InlineData("false and false or true", true)]
        [InlineData("(true or false) and false", false)]
        [InlineData("false and (false or true)", false)]
        [InlineData("((true))", true)]
        [InlineData("( '$(A)' == '1' and '$(B)' == '2' )", true)]
        [InlineData("'$(Configuration)' == 'Release' or ('$(A)' == '1' and !('$(B)' == '3'))", true)]
        public void PrecedenceAndParentheses(string condition, bool? expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Theory]
        [InlineData("Exists('present.props')", true)]
        [InlineData("exists('present.props')", true)]
        [InlineData("EXISTS ( 'present.props' )", true)]
        [InlineData("Exists('missing.props')", false)]
        [InlineData("Exists('$(Dir)')", true)]
        [InlineData("Exists($(Dir))", true)]
        [InlineData("Exists('unknowable')", null)]
        [InlineData("Exists('$(Undefined)')", null)]
        [InlineData("Exists('present.props') and '$(A)' == '1'", true)]
        [InlineData("'$(A)' == '1' and Exists('missing.props')", false)]
        // Functions other than Exists are not evaluated.
        [InlineData("HasTrailingSlash('$(Dir)')", null)]
        [InlineData("HasTrailingSlash('$(Dir)') or true", true)]
        public void Functions(string condition, bool? expected)
        {
            Assert.Equal(expected, Evaluate(condition));
        }

        [Fact]
        public void Exists_ReceivesTheExpandedArgument()
        {
            var paths = new List<string>();

            var result = MSBuildCondition.Evaluate("Exists('$(Dir)props/$(A).props')", Expand, path =>
            {
                paths.Add(path);
                return true;
            });

            Assert.True(result);
            Assert.Equal(new[] { "src/props/1.props" }, paths);
        }

        [Fact]
        public void Expand_ReceivesTheRawOperandText()
        {
            var operands = new List<string>();

            MSBuildCondition.Evaluate("'$(A)' == x$(B)y and $(C)", text =>
            {
                operands.Add(text);
                return text;
            }, _ => null);

            Assert.Equal(new[] { "$(A)", "x$(B)y", "$(C)" }, operands);
        }

        [Theory]
        // A property that can't be expanded makes the comparison unknown.
        [InlineData("'$(Undefined)' == ''")]
        [InlineData("'' != '$(Undefined)'")]
        [InlineData("$(Undefined)")]
        [InlineData("'$(Undefined)' == '' and true")]
        public void UnexpandableProperty_IsUnknown(string condition)
        {
            Assert.Null(Evaluate(condition));
        }

        [Theory]
        // Numeric comparisons and a single '=' are not supported.
        [InlineData("'$(A)' < '2'")]
        [InlineData("'$(A)' > '0'")]
        [InlineData("'$(A)' >= '1'")]
        [InlineData("'$(A)' <= '1'")]
        [InlineData("'$(A)' = '1'")]
        // Unbalanced quotes, property references and parentheses.
        [InlineData("'$(A) == '1'")]
        [InlineData("'unterminated")]
        [InlineData("$(A == '1'")]
        [InlineData("('1' == '1'")]
        [InlineData("'1' == '1')")]
        [InlineData("Exists('present.props'")]
        [InlineData("false and Exists('present.props'")]
        [InlineData("Exists('present.props', 'x')")]
        [InlineData("Exists()")]
        // Dangling or misplaced operators, at the end of the text too (where a lookahead would run past it).
        [InlineData("'1' =")]
        [InlineData("'1' ==")]
        [InlineData("false and '1' ==")]
        [InlineData("'1' == '1' !")]
        [InlineData("!")]
        [InlineData("true and")]
        [InlineData("false and")]
        [InlineData("true or")]
        [InlineData("and true")]
        [InlineData("()")]
        [InlineData("== '1'")]
        [InlineData("'1' == '1' '2'")]
        [InlineData("true true")]
        [InlineData("'a' , 'b'")]
        [InlineData("true,")]
        public void UnparseableCondition_IsUnknown(string condition)
        {
            Assert.Null(Evaluate(condition));
        }
    }
}
