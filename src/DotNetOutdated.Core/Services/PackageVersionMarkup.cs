using System;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Text-level edits to package version attributes in MSBuild files that keep the rest of the file byte-for-byte.
    /// </summary>
    internal static class PackageVersionMarkup
    {
        internal static readonly string[] CentralPackageElements = ["PackageVersion", "GlobalPackageReference"];

        // One complete attribute: name, '=', and a whole quoted value (which may contain '>' or the other quote).
        private const string AttributePattern = @"[^\s=/>]+\s*=\s*(?:""[^""]*""|'[^']*')";

        private static readonly Regex Attribute = new(
            @"(?<name>[^\s=/>]+)\s*=\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

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
        public static string ReplaceVersion(string content, string[] elementNames, string packageName, string newValue, out bool matched)
        {
            var elements = string.Join("|", Array.ConvertAll(elementNames, Regex.Escape));

            // Inactive markup is matched first so that it is skipped as a whole; start tags are tokenized into
            // complete quoted attributes so that text inside another attribute's value is never mistaken for one.
            var pattern =
                @"<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|" +
                $@"<(?:{elements})(?<attributes>(?:\s+{AttributePattern})*)\s*/?>";

            var found = false;
            var result = Regex.Replace(
                content,
                pattern,
                m =>
                {
                    var attributes = m.Groups["attributes"];
                    if (!attributes.Success)
                    {
                        return m.Value;
                    }

                    Group version = null;
                    var isPackage = false;
                    foreach (Match attribute in Attribute.Matches(attributes.Value))
                    {
                        var name = attribute.Groups["name"].Value;
                        var value = attribute.Groups["value"];
                        if (name is "Include" or "Update")
                        {
                            isPackage |= string.Equals(value.Value.Trim(), packageName, StringComparison.OrdinalIgnoreCase);
                        }
                        else if (name == "Version" && version == null)
                        {
                            version = value;
                        }
                    }

                    if (!isPackage || version == null)
                    {
                        return m.Value;
                    }

                    found = true;
                    var start = attributes.Index - m.Index + version.Index;
                    return string.Concat(m.Value.AsSpan(0, start), newValue, m.Value.AsSpan(start + version.Length));
                },
                RegexOptions.CultureInvariant);
            matched = found;
            return result;
        }
    }
}
