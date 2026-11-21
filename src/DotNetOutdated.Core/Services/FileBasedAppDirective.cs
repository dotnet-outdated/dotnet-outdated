using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// A <c>#:sdk</c>, <c>#:package</c>, <c>#:property</c>, <c>#:include</c> or <c>#:exclude</c> line of a file-based app,
/// parsed as the SDK parses it: the text after the kind is split into whitespace-separated tokens, the first holding the
/// name and (after the kind's separator, <c>@</c> or <c>=</c>, which whitespace may surround) the value, and the rest
/// <c>Name=Value</c> item metadata. Either side of a separator is bare or a C# string literal in double quotes, whose
/// escapes are decoded. A directive without quotes whose trailing tokens aren't all metadata is read in the SDK's legacy
/// form, its whole text after the separator taken as the value. As dotnet-outdated always has, a <c>//</c> comment that
/// follows whitespace at the end of the line is left out.
/// </summary>
/// <param name="Line">The line the directive was read from.</param>
/// <param name="Kind">The directive kind, such as <c>package</c>.</param>
/// <param name="Name">The id, property name or path, decoded.</param>
/// <param name="Value">The version or property value, decoded; <see langword="null"/> when there is no separator.</param>
/// <param name="ValueSpan">Where the value is written in <paramref name="Line"/>, quotes included; empty when there is none.</param>
internal sealed record FileBasedAppDirective(
    string Line,
    string Kind,
    string Name,
    string? Value,
    IReadOnlyList<KeyValuePair<string, string>> Metadata,
    (int Start, int End) ValueSpan)
{
    /// <summary>
    /// Parses a directive line; <see langword="false"/> for another kind of line, or one the SDK would reject (an
    /// unterminated or misplaced quote, an unknown escape, a missing name, extra text, or metadata that isn't valid).
    /// </summary>
    public static bool TryParse(string line, [NotNullWhen(true)] out FileBasedAppDirective? directive)
    {
        directive = null;
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (!line.AsSpan(start).StartsWith("#:", StringComparison.Ordinal))
        {
            return false;
        }

        var (contentStart, contentEnd) = Trim(line, start + 2, ContentEnd(line, start + 2));
        var kindEnd = contentStart;
        while (kindEnd < contentEnd && !char.IsWhiteSpace(line[kindEnd]))
        {
            kindEnd++;
        }

        var kind = line[contentStart..kindEnd];
        var (textStart, _) = Trim(line, kindEnd, contentEnd);
        var text = line[textStart..contentEnd];
        var (separator, allowsMetadata, singleToken) = kind switch
        {
            "package" => ((char?)'@', true, false),
            "sdk" => ('@', false, true),
            "property" => ('=', false, true),
            "include" => (null, true, false),
            "exclude" => (null, false, true),
            _ => ((char?)null, false, false),
        };

        if (kind is not ("package" or "sdk" or "property" or "include" or "exclude") ||
            Tokenize(text, separator, allowsMetadata) is not { } tokenized ||
            tokenized.Tokens.Count == 0 ||
            (singleToken && tokenized.Tokens.Count > 1) ||
            ParseMetadata(tokenized.Tokens) is not { } metadata)
        {
            return false;
        }

        var first = tokenized.Tokens[0];
        var name = first.SeparatorIndex < 0 ? first.Text : first.Text[..first.SeparatorIndex];
        string? value = first.SeparatorIndex < 0 ? null : first.Text[(first.SeparatorIndex + 1)..];
        var valueStart = first.RawSeparator < 0 ? first.RawEnd : first.RawSeparator + 1;
        if (tokenized.IsLegacy)
        {
            // The legacy form keeps the whitespace inside its value, but not that around the separator.
            name = name.TrimEnd();
            value = value?.TrimStart();
        }

        (valueStart, _) = Trim(text, valueStart, first.RawEnd);

        if (name.Length == 0 ||
            (separator != null && name.Any(c => char.IsWhiteSpace(c) || c is '@' or '=' or '/')) ||
            (kind == "property" && (value == null || !IsValidMSBuildName(name))) ||
            (kind == "package" && value != null && metadata.Any(pair => string.Equals(pair.Key, "Version", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        directive = new FileBasedAppDirective(
            line,
            kind,
            name,
            value,
            metadata,
            (textStart + valueStart, textStart + first.RawEnd));
        return true;
    }

    /// <summary>
    /// The line with its value replaced by <paramref name="value"/>: quoted if the value was, since without quotes
    /// the SDK can read the line in its legacy form (taking the metadata after it as part of the value), and otherwise
    /// quoted if it needs to be.
    /// </summary>
    public string WithValue(string value)
    {
        var quoted = ValueSpan.End > ValueSpan.Start && Line[ValueSpan.Start] == '"';
        return Line[..ValueSpan.Start] + (quoted ? Quote(value) : QuoteIfNeeded(value)) + Line[ValueSpan.End..];
    }

    /// <summary>
    /// The value as a directive writes it: bare, or as a C# string literal when it holds whitespace or a quote.
    /// </summary>
    public static string QuoteIfNeeded(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c == '"') ? Quote(value) : value;

    // The value as a C# string literal.
    private static string Quote(string value)
    {

        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\0' => "\\0",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Reads the C# string literal at <paramref name="start"/> (a double quote): its escapes are decoded, and
    /// <paramref name="end"/> is the index after its closing quote. <see langword="false"/> when the quote isn't closed
    /// on the line or an escape isn't valid.
    /// </summary>
    public static bool TryReadStringLiteral(string text, int start, out string value, out int end)
    {
        var builder = new StringBuilder();
        value = string.Empty;
        end = -1;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                value = builder.ToString();
                end = i + 1;
                return true;
            }

            if (c is '\r' or '\n')
            {
                return false;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (++i >= text.Length)
            {
                return false;
            }

            switch (text[i])
            {
                case '\'': builder.Append('\''); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '0': builder.Append('\0'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'e': builder.Append('\u001b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'v': builder.Append('\v'); break;
                case 'x' or 'u' or 'U':
                    // \x takes one to four hex digits, \u exactly four and \U exactly eight.
                    var (min, max) = text[i] switch { 'x' => (1, 4), 'u' => (4, 4), _ => (8, 8) };
                    var digits = 0;
                    while (digits < max && i + 1 + digits < text.Length && char.IsAsciiHexDigit(text[i + 1 + digits]))
                    {
                        digits++;
                    }

                    if (digits < min ||
                        !int.TryParse(text.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ||
                        code > 0x10FFFF)
                    {
                        return false;
                    }

                    builder.Append(code <= char.MaxValue ? ((char)code).ToString() : char.ConvertFromUtf32(code));
                    i += digits;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    // One whitespace-separated token: its decoded text and where its separator is in it, and where it and its
    // separator are written in the directive's text (-1 when it has none).
    private readonly record struct Token(string Text, int SeparatorIndex, int RawStart, int RawEnd, int RawSeparator);

    private sealed record TokenizedText(List<Token> Tokens, bool IsLegacy);

    // As the SDK's TokenizeWithLegacyFallback: quotes mean the new form, parsed strictly; otherwise the tokens are
    // split on whitespace when there is one, or when the directive takes metadata and every trailing token is
    // metadata, and the whole text is one legacy token when not.
    private static TokenizedText? Tokenize(string text, char? nameSeparator, bool allowsMetadata)
    {
        if (text.Contains('"', StringComparison.Ordinal))
        {
            return TokenizeStrictly(text, nameSeparator, allowsMetadata) is { } strict ? new TokenizedText(strict, false) : null;
        }

        var raw = new List<(int Start, int End)>();
        for (var i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > start)
            {
                raw.Add((start, i));
            }
        }

        if (raw.Count <= 1 || (allowsMetadata && raw.Skip(1).All(span => IsMetadataToken(text[span.Start..span.End]))))
        {
            return new TokenizedText(
                [.. raw.Select((span, index) => BareToken(text, span.Start, span.End, index == 0 ? nameSeparator : '='))],
                false);
        }

        return new TokenizedText([BareToken(text, 0, text.Length, nameSeparator)], true);
    }

    private static Token BareToken(string text, int start, int end, char? separator)
    {
        var separatorIndex = separator is { } s ? text.IndexOf(s, start, end - start) : -1;
        return new Token(text[start..end], separatorIndex < 0 ? -1 : separatorIndex - start, start, end, separatorIndex);
    }

    // As the SDK's Tokenize: whitespace ends a token except around its separator (and after the separator, before a
    // value that isn't metadata); a quoted part is a whole token or one side of its separator.
    private static List<Token>? TokenizeStrictly(string text, char? nameSeparator, bool allowsMetadata)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var tokenStarted = false;
        var quoteClosed = false;
        var separatorIndex = -1;
        var rawStart = -1;
        var rawSeparator = -1;
        var afterSeparator = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var separator = tokens.Count == 0 ? nameSeparator : '=';

            if (c == '"')
            {
                if (quoteClosed || !(current.Length == 0 || afterSeparator) ||
                    !TryReadStringLiteral(text, i, out var literal, out var end))
                {
                    return null;
                }

                current.Append(literal);
                if (!tokenStarted)
                {
                    rawStart = i;
                }

                tokenStarted = true;
                quoteClosed = true;
                afterSeparator = false;
                i = end - 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var beforeValue = afterSeparator && !(allowsMetadata && StartsMetadataToken(text, i + 1));
                if (tokenStarted && (beforeValue || IsNextNonWhiteSpace(text, i, separator, separatorIndex)))
                {
                    continue;
                }

                if (tokenStarted)
                {
                    tokens.Add(new Token(current.ToString(), separatorIndex, rawStart, i, rawSeparator));
                    current.Clear();
                    tokenStarted = false;
                    quoteClosed = false;
                    separatorIndex = -1;
                    rawSeparator = -1;
                    afterSeparator = false;
                }

                continue;
            }

            if (separator is { } s && c == s && separatorIndex < 0)
            {
                separatorIndex = current.Length;
                rawSeparator = i;
                current.Append(c);
                if (!tokenStarted)
                {
                    rawStart = i;
                }

                tokenStarted = true;
                quoteClosed = false;
                afterSeparator = true;
                continue;
            }

            if (quoteClosed)
            {
                return null;
            }

            current.Append(c);
            if (!tokenStarted)
            {
                rawStart = i;
            }

            tokenStarted = true;
            afterSeparator = false;
        }

        if (tokenStarted)
        {
            tokens.Add(new Token(current.ToString(), separatorIndex, rawStart, text.Length, rawSeparator));
        }

        return tokens;

        static bool IsNextNonWhiteSpace(string text, int index, char? separator, int separatorIndex)
        {
            if (separator is not { } s || separatorIndex >= 0)
            {
                return false;
            }

            for (var i = index + 1; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return text[i] == s;
                }
            }

            return false;
        }

        static bool StartsMetadataToken(string text, int index)
        {
            var start = index;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '=')
            {
                end++;
            }

            if (end == start)
            {
                return false;
            }

            var name = text[start..end];
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            return end < text.Length && text[end] == '=' && IsValidMSBuildName(name);
        }
    }

    // The Name=Value pairs after the first token; null when one isn't valid metadata, or a name repeats.
    private static List<KeyValuePair<string, string>>? ParseMetadata(List<Token> tokens)
    {
        var metadata = new List<KeyValuePair<string, string>>();
        foreach (var token in tokens.Skip(1))
        {
            if (token.SeparatorIndex <= 0)
            {
                return null;
            }

            var name = token.Text[..token.SeparatorIndex];
            if (!IsValidMSBuildName(name) || metadata.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            metadata.Add(new(name, token.Text[(token.SeparatorIndex + 1)..]));
        }

        return metadata;
    }

    private static bool IsMetadataToken(string token)
    {
        var separator = token.IndexOf('=', StringComparison.Ordinal);
        return separator > 0 && IsValidMSBuildName(token[..separator]);
    }

    // A valid XML NCName, which MSBuild requires of property, item type and metadata names.
    public static bool IsValidMSBuildName(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    // Where the directive's text ends: before a // comment that follows whitespace, outside quotes.
    private static int ContentEnd(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (!TryReadStringLiteral(line, i, out _, out var end))
                {
                    return line.Length;
                }

                i = end - 1;
            }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/' && i > start && char.IsWhiteSpace(line[i - 1]))
            {
                return i;
            }
        }

        return line.Length;
    }

    private static (int Start, int End) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }
}
