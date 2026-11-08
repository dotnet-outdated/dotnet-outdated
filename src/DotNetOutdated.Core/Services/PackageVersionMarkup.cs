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
        /// for <paramref name="packageName"/> while leaving any other variable-backed elements untouched.
        /// </summary>
        public static string ReplacePackageVersion(string content, string[] elementNames, string packageName, string newValue)
        {
            var result = ReplaceVersionAttributes(
                content, elementNames, packageName, newValue, allowVersionOverride: true, skipExistingVariables: true, out _);

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
                        vm => !vm.Groups["vopen"].Success || vm.Groups["vval"].Value.Contains("$(", StringComparison.Ordinal)
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
            out bool matched)
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
                        (skipExistingVariables && version.Value.Contains("$(", StringComparison.Ordinal)))
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

        /// <summary>
        /// Replaces the value of <paramref name="propertyName"/> inside <c>&lt;PropertyGroup&gt;</c> elements,
        /// preserving any attributes on the property tag, tag casing, and surrounding whitespace.
        /// </summary>
        public static string ReplacePropertyValue(string content, string propertyName, string oldValue, string newValue)
        {
            var propertyPattern = new Regex(
                InactiveMarkupPattern + @"|(?<open><(?<tag>" + Regex.Escape(propertyName) + @")(?:\s+" + AttributePattern + @")*\s*>)(?<value>[\s\S]*?)(?<close></\k<tag>\s*>)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var expectedTrimmed = (oldValue ?? string.Empty).Trim();

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
                        propMatch => !propMatch.Groups["open"].Success ||
                                     !string.Equals(propMatch.Groups["value"].Value.Trim(), expectedTrimmed, StringComparison.Ordinal)
                            ? propMatch.Value
                            : propMatch.Groups["open"].Value +
                              ReplaceTrimmed(propMatch.Groups["value"].Value, newValue) +
                              propMatch.Groups["close"].Value);

                    return groupMatch.Groups["open"].Value + newBody + groupMatch.Groups["close"].Value;
                });
        }

        /// <summary>
        /// Inserts <c>&lt;propertyName&gt;newValue&lt;/propertyName&gt;</c> into the first <c>&lt;PropertyGroup&gt;</c>,
        /// or creates a <c>&lt;PropertyGroup&gt;</c> under <c>&lt;Project&gt;</c> when none exists.
        /// </summary>
        public static string InsertProperty(string content, string propertyName, string newValue)
        {
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var inserted = false;

            var withPropertyGroup = PropertyGroupPattern.Replace(
                content,
                m =>
                {
                    if (inserted || !m.Groups["open"].Success)
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
