using System;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Text-level edits to package version attributes and MSBuild properties that keep the rest of the file byte-for-byte.
    /// </summary>
    internal static class PackageVersionMarkup
    {
        internal static readonly string[] CentralPackageElements = ["PackageVersion", "GlobalPackageReference"];

        // One complete attribute: name, '=', and a whole quoted value (which may contain '>' or the other quote).
        private const string AttributePattern = @"[^\s=/>]+\s*=\s*(?:""[^""]*""|'[^']*')";
        private const string InactiveMarkupPattern = @"<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>";

        private static readonly Regex Attribute = new(
            @"(?<name>[^\s=/>]+)\s*=\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex PropertyGroupPattern = new(
            InactiveMarkupPattern + @"|(?<open><PropertyGroup(?:\s+" + AttributePattern + @")*\s*>)(?<body>[\s\S]*?)(?<close></PropertyGroup\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ProjectOpenTagPattern = new(
            InactiveMarkupPattern + @"|(?<open><Project(?:\s+" + AttributePattern + @")*\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces the <c>Version</c> attribute value of every <paramref name="elementNames"/> element whose
        /// <c>Include</c> or <c>Update</c> is <paramref name="packageName"/> (case-insensitive). Attributes may be
        /// in any order, use either quote style, have spaces around <c>=</c> and span several lines.
        /// Markup inside comments, CDATA sections and processing instructions is left untouched.
        /// </summary>
        public static string ReplaceVersion(string content, string[] elementNames, string packageName, string newValue) =>
            ReplaceVersion(content, elementNames, packageName, newValue, out _);

        /// <inheritdoc cref="ReplaceVersion(string, string[], string, string)"/>
        /// <param name="matched">True when at least one element matched, even if it already had <paramref name="newValue"/>.</param>
        public static string ReplaceVersion(string content, string[] elementNames, string packageName, string newValue, out bool matched) =>
            ReplaceVersionAttributes(content, elementNames, packageName, newValue, allowVersionOverride: false, skipExistingVariables: false, out matched);

        /// <summary>
        /// Restores a variable reference in <c>Version</c> or <c>VersionOverride</c> attributes or child elements
        /// for <paramref name="packageName"/>. Values that contain a property reference are left untouched unless they
        /// equal <paramref name="previousExpression"/> (the expression being rewritten).
        /// </summary>
        public static string ReplacePackageVersion(string content, string[] elementNames, string packageName, string newValue, string previousExpression = null)
        {
            var result = ReplaceVersionAttributes(
                content, elementNames, packageName, newValue, allowVersionOverride: true, skipExistingVariables: true, out _, previousExpression);

            var elements = string.Join("|", Array.ConvertAll(elementNames, Regex.Escape));
            var childElementPattern =
                InactiveMarkupPattern + "|" +
                $@"(?<open><(?<elem>{elements})(?<attributes>(?:\s+{AttributePattern})*)\s*>)(?<body>[\s\S]*?)(?<close></\k<elem>\s*>)";
            var childVersionPattern =
                InactiveMarkupPattern + "|" +
                $@"(?<vopen><(?<vtag>VersionOverride|Version)(?:\s+{AttributePattern})*\s*>)(?<vval>[\s\S]*?)(?<vclose></\k<vtag>\s*>)";

            return Regex.Replace(
                result,
                childElementPattern,
                m =>
                {
                    if (!m.Groups["open"].Success ||
                        !MatchesPackage(m.Groups["attributes"].Value, packageName, allowVersionOverride: true, out var versionAttr) ||
                        versionAttr != null)
                    {
                        return m.Value;
                    }

                    var newBody = Regex.Replace(
                        m.Groups["body"].Value,
                        childVersionPattern,
                        vm => !vm.Groups["vopen"].Success || IsOtherExpression(vm.Groups["vval"].Value, previousExpression)
                            ? vm.Value
                            : vm.Groups["vopen"].Value + ReplaceTrimmed(vm.Groups["vval"].Value, newValue) + vm.Groups["vclose"].Value,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                    return m.Groups["open"].Value + newBody + m.Groups["close"].Value;
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string ReplaceVersionAttributes(
            string content,
            string[] elementNames,
            string packageName,
            string newValue,
            bool allowVersionOverride,
            bool skipExistingVariables,
            out bool matched,
            string previousExpression = null)
        {
            var elements = string.Join("|", Array.ConvertAll(elementNames, Regex.Escape));
            var pattern =
                InactiveMarkupPattern + "|" +
                $@"<(?:{elements})(?<attributes>(?:\s+{AttributePattern})*)\s*/?>";

            var found = false;
            var result = Regex.Replace(
                content,
                pattern,
                m =>
                {
                    var attributes = m.Groups["attributes"];
                    if (!attributes.Success ||
                        !MatchesPackage(attributes.Value, packageName, allowVersionOverride, out var version) ||
                        version == null ||
                        (skipExistingVariables && IsOtherExpression(version.Value, previousExpression)))
                    {
                        return m.Value;
                    }

                    found = true;
                    var start = attributes.Index - m.Index + version.Index;
                    return string.Concat(m.Value.AsSpan(0, start), newValue, m.Value.AsSpan(start + version.Length));
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            matched = found;
            return result;
        }

        private static bool IsOtherExpression(string value, string previousExpression) =>
            value.Contains("$(", StringComparison.Ordinal) &&
            !(previousExpression != null && string.Equals(value.Trim(), previousExpression.Trim(), StringComparison.Ordinal));

        private static bool MatchesPackage(string attributesText, string packageName, bool allowVersionOverride, out Group version)
        {
            Group versionAttr = null;
            Group overrideAttr = null;
            var isPackage = false;

            foreach (Match attribute in Attribute.Matches(attributesText))
            {
                var name = attribute.Groups["name"].Value;
                var value = attribute.Groups["value"];
                if (name.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    isPackage |= string.Equals(value.Value.Trim(), packageName, StringComparison.OrdinalIgnoreCase);
                }
                else if (name.Equals("Version", StringComparison.OrdinalIgnoreCase) && versionAttr == null)
                {
                    versionAttr = value;
                }
                else if (allowVersionOverride && name.Equals("VersionOverride", StringComparison.OrdinalIgnoreCase) && overrideAttr == null)
                {
                    overrideAttr = value;
                }
            }

            version = overrideAttr ?? versionAttr;
            return isPackage;
        }

        private static Regex PropertyElementPattern(string propertyName) => new(
            InactiveMarkupPattern +
            @"|(?<open><(?<tag>" + Regex.Escape(propertyName) + @")(?<attributes>(?:\s+" + AttributePattern + @")*)\s*>)(?<value>[\s\S]*?)(?<close></\k<tag>\s*>)" +
            @"|(?<empty><(?<etag>" + Regex.Escape(propertyName) + @")(?<eattributes>(?:\s+" + AttributePattern + @")*)\s*/>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces the value of the <paramref name="elementIndex"/>-th (zero-based, document order)
        /// <paramref name="propertyName"/> element directly inside a <c>&lt;PropertyGroup&gt;</c>, preserving its
        /// attributes, tag casing and surrounding whitespace. Other definitions of the property are left alone.
        /// </summary>
        public static string ReplacePropertyValueAt(string content, string propertyName, int elementIndex, string newValue)
        {
            var propertyPattern = PropertyElementPattern(propertyName);
            var index = 0;

            return PropertyGroupPattern.Replace(
                content,
                groupMatch =>
                {
                    if (!groupMatch.Groups["open"].Success)
                    {
                        return groupMatch.Value;
                    }

                    var newBody = propertyPattern.Replace(
                        groupMatch.Groups["body"].Value,
                        propMatch =>
                        {
                            if (!propMatch.Groups["open"].Success && !propMatch.Groups["empty"].Success)
                            {
                                return propMatch.Value;
                            }

                            if (index++ != elementIndex)
                            {
                                return propMatch.Value;
                            }

                            return propMatch.Groups["open"].Success
                                ? propMatch.Groups["open"].Value + ReplaceTrimmed(propMatch.Groups["value"].Value, newValue) + propMatch.Groups["close"].Value
                                : $"<{propMatch.Groups["etag"].Value}{propMatch.Groups["eattributes"].Value}>{newValue}</{propMatch.Groups["etag"].Value}>";
                        });

                    return groupMatch.Groups["open"].Value + newBody + groupMatch.Groups["close"].Value;
                });
        }

        /// <summary>
        /// Inserts <c>&lt;propertyName&gt;newValue&lt;/propertyName&gt;</c> at the end of the
        /// <paramref name="propertyGroupIndex"/>-th (zero-based, document order, non-empty) <c>&lt;PropertyGroup&gt;</c>,
        /// or, when the index is negative, in a new <c>&lt;PropertyGroup&gt;</c> at the start of <c>&lt;Project&gt;</c>.
        /// </summary>
        public static string InsertProperty(string content, int propertyGroupIndex, string propertyName, string newValue)
        {
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var inserted = false;

            if (propertyGroupIndex >= 0)
            {
                var index = 0;
                var withPropertyGroup = PropertyGroupPattern.Replace(
                    content,
                    m =>
                    {
                        if (!m.Groups["open"].Success || index++ != propertyGroupIndex)
                        {
                            return m.Value;
                        }

                        inserted = true;
                        var body = m.Groups["body"].Value;
                        var lastLf = body.LastIndexOf('\n');
                        if (lastLf < 0)
                        {
                            return $"{m.Groups["open"].Value}{body}<{propertyName}>{newValue}</{propertyName}>{m.Groups["close"].Value}";
                        }

                        var prefix = body[..(lastLf > 0 && body[lastLf - 1] == '\r' ? lastLf - 1 : lastLf)];
                        var closingIndent = body[(lastLf + 1)..];
                        var prevLf = prefix.LastIndexOf('\n');
                        var prevLine = prevLf >= 0 ? prefix[(prevLf + 1)..] : string.Empty;
                        var childIndent = prevLine[..(prevLine.Length - prevLine.AsSpan().TrimStart().Length)];
                        if (childIndent.Length == 0)
                        {
                            childIndent = closingIndent + (closingIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "  ");
                        }

                        return $"{m.Groups["open"].Value}{prefix}{newline}{childIndent}<{propertyName}>{newValue}</{propertyName}>{newline}{closingIndent}{m.Groups["close"].Value}";
                    });

                if (inserted)
                {
                    return withPropertyGroup;
                }
            }

            return ProjectOpenTagPattern.Replace(
                content,
                m =>
                {
                    if (inserted || !m.Groups["open"].Success)
                    {
                        return m.Value;
                    }

                    inserted = true;
                    var after = m.Index + m.Length;
                    return after < content.Length && content[after] is '\r' or '\n'
                        ? $"{m.Value}{newline}  <PropertyGroup>{newline}    <{propertyName}>{newValue}</{propertyName}>{newline}  </PropertyGroup>"
                        : $"{m.Value}<PropertyGroup><{propertyName}>{newValue}</{propertyName}></PropertyGroup>";
                });
        }

        /// <summary>
        /// Inserts <c>&lt;propertyName&gt;newValue&lt;/propertyName&gt;</c> immediately before the
        /// <paramref name="anchorIndex"/>-th <paramref name="anchorName"/> property element (see
        /// <see cref="ReplacePropertyValueAt"/>), on its own line with the same indentation when the anchor starts a line.
        /// Returns <paramref name="content"/> unchanged when the anchor isn't found.
        /// </summary>
        public static string InsertPropertyBefore(string content, string anchorName, int anchorIndex, string propertyName, string newValue)
        {
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var anchorPattern = PropertyElementPattern(anchorName);
            var index = 0;

            return PropertyGroupPattern.Replace(
                content,
                groupMatch =>
                {
                    if (!groupMatch.Groups["open"].Success)
                    {
                        return groupMatch.Value;
                    }

                    var body = groupMatch.Groups["body"].Value;
                    var newBody = anchorPattern.Replace(
                        body,
                        propMatch =>
                        {
                            if ((!propMatch.Groups["open"].Success && !propMatch.Groups["empty"].Success) || index++ != anchorIndex)
                            {
                                return propMatch.Value;
                            }

                            var lineStart = body.LastIndexOf('\n', Math.Max(propMatch.Index - 1, 0)) + 1;
                            var leading = body[lineStart..propMatch.Index];
                            var element = $"<{propertyName}>{newValue}</{propertyName}>";
                            return lineStart > 0 && string.IsNullOrWhiteSpace(leading)
                                ? $"{element}{newline}{leading}{propMatch.Value}"
                                : $"{element}{propMatch.Value}";
                        });

                    return groupMatch.Groups["open"].Value + newBody + groupMatch.Groups["close"].Value;
                });
        }

        private static string ReplaceTrimmed(string value, string newValue)
        {
            var span = value.AsSpan();
            var lead = span.Length - span.TrimStart().Length;
            var trail = span.Length - span.TrimEnd().Length;
            return lead + trail >= span.Length
                ? newValue
                : string.Concat(span[..lead], newValue, span[(span.Length - trail)..]);
        }
    }
}
