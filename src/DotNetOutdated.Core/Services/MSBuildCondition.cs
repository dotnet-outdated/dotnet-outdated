using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Statically evaluates the subset of MSBuild condition syntax that appears in version-property definitions:
/// <c>==</c> / <c>!=</c> string comparisons, <c>and</c>, <c>or</c>, <c>!</c>, parentheses, boolean literals and
/// <c>Exists('path')</c>. Anything else, or any value that can't be expanded statically, makes the result
/// <see langword="null"/> (unknown) rather than a guess.
/// </summary>
internal static class MSBuildCondition
{
    /// <param name="condition">The raw <c>Condition</c> attribute value.</param>
    /// <param name="expand">Expands property references in a value, or returns <see langword="null"/> when it can't.</param>
    /// <param name="exists">Tests an expanded path for <c>Exists()</c>, or returns <see langword="null"/> when it can't.</param>
    public static bool? Evaluate(string condition, Func<string, string?> expand, Func<string, bool?> exists)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var tokens = Tokenize(condition);
        if (tokens == null)
        {
            return null;
        }

        var parser = new Parser(tokens, expand, exists);
        var result = parser.ParseOr();
        return parser.AtEnd && !parser.Failed ? result : null;
    }

    private enum TokenKind { Quoted, Word, Open, Close, Not, Equal, NotEqual, Comma }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token>? Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    return null;
                }

                tokens.Add(new Token(TokenKind.Quoted, text[(i + 1)..end]));
                i = end + 1;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.Close, ")"));
                i++;
            }
            else if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
            }
            else if (c == '=' && i + 1 < text.Length && text[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.Equal, "=="));
                i += 2;
            }
            else if (c == '!' && i + 1 < text.Length && text[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.NotEqual, "!="));
                i += 2;
            }
            else if (c == '!')
            {
                tokens.Add(new Token(TokenKind.Not, "!"));
                i++;
            }
            else if (c is '<' or '>' or '=')
            {
                // Numeric comparisons are not supported.
                return null;
            }
            else
            {
                var word = new StringBuilder();
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('\'' or '(' or ')' or '!' or '=' or '<' or '>' or ','))
                {
                    if (text[i] is '$' or '@' or '%' && i + 1 < text.Length && text[i + 1] == '(')
                    {
                        var close = text.IndexOf(')', i);
                        if (close < 0)
                        {
                            return null;
                        }

                        word.Append(text, i, close - i + 1);
                        i = close + 1;
                    }
                    else
                    {
                        word.Append(text[i++]);
                    }
                }

                tokens.Add(new Token(TokenKind.Word, word.ToString()));
            }
        }

        return tokens;
    }

    private sealed class Parser(List<Token> tokens, Func<string, string?> expand, Func<string, bool?> exists)
    {
        private int _position;

        public bool Failed { get; private set; }

        public bool AtEnd => _position >= tokens.Count;

        private Token? Peek => _position < tokens.Count ? tokens[_position] : null;

        public bool? ParseOr()
        {
            var left = ParseAnd();
            while (IsKeyword("or"))
            {
                _position++;
                var right = ParseAnd();
                left = left == true || right == true ? true : left == null || right == null ? null : false;
            }

            return left;
        }

        private bool? ParseAnd()
        {
            var left = ParseUnary();
            while (IsKeyword("and"))
            {
                _position++;
                var right = ParseUnary();
                left = left == false || right == false ? false : left == null || right == null ? null : true;
            }

            return left;
        }

        private bool? ParseUnary()
        {
            if (Peek?.Kind == TokenKind.Not)
            {
                _position++;
                var operand = ParseUnary();
                return operand == null ? null : !operand;
            }

            return ParsePrimary();
        }

        private bool? ParsePrimary()
        {
            var token = Peek;
            if (token == null)
            {
                Failed = true;
                return null;
            }

            if (token.Value.Kind == TokenKind.Open)
            {
                _position++;
                var inner = ParseOr();
                Expect(TokenKind.Close);
                return inner;
            }

            if (token.Value.Kind == TokenKind.Word &&
                _position + 1 < tokens.Count &&
                tokens[_position + 1].Kind == TokenKind.Open)
            {
                return ParseFunction(token.Value.Text);
            }

            var left = ParseOperand();
            var op = Peek;
            if (op?.Kind is TokenKind.Equal or TokenKind.NotEqual)
            {
                _position++;
                var right = ParseOperand();
                if (left == null || right == null)
                {
                    return null;
                }

                var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                return op.Value.Kind == TokenKind.Equal ? equal : !equal;
            }

            if (left == null)
            {
                return null;
            }

            return left.ToUpperInvariant() switch
            {
                "TRUE" or "ON" or "YES" => true,
                "FALSE" or "OFF" or "NO" => false,
                _ => null,
            };
        }

        private bool? ParseFunction(string name)
        {
            _position += 2; // name and '('
            var argument = ParseOperand();
            Expect(TokenKind.Close);
            if (!name.Equals("Exists", StringComparison.OrdinalIgnoreCase) || argument == null)
            {
                return null;
            }

            return exists(argument);
        }

        private string? ParseOperand()
        {
            var token = Peek;
            if (token?.Kind is not (TokenKind.Quoted or TokenKind.Word))
            {
                Failed = true;
                return null;
            }

            _position++;
            return expand(token.Value.Text);
        }

        private bool IsKeyword(string keyword) =>
            Peek is { Kind: TokenKind.Word } token && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private void Expect(TokenKind kind)
        {
            if (Peek?.Kind == kind)
            {
                _position++;
            }
            else
            {
                Failed = true;
            }
        }
    }
}
