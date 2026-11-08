using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Text-level edits to package version attributes in MSBuild files that keep the rest of the file byte-for-byte.
    /// </summary>
    internal static class PackageVersionMarkup
    {
        internal static readonly string[] CentralPackageElements = ["PackageVersion", "GlobalPackageReference"];

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
            var elements = string.Join("|", System.Array.ConvertAll(elementNames, Regex.Escape));
            var pattern =
                @"<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|" +
                $@"(?<prefix><(?:{elements})\b(?=[^>]*\b(?:Include|Update)\s*=\s*(?<packageQuote>[""'])(?i:{Regex.Escape(packageName)})\k<packageQuote>)[^>]*\bVersion\s*=\s*(?<versionQuote>[""']))[^""']*(?<suffix>\k<versionQuote>)";

            var found = false;
            var result = Regex.Replace(
                content,
                pattern,
                m =>
                {
                    if (!m.Groups["prefix"].Success)
                    {
                        return m.Value;
                    }

                    found = true;
                    return $"{m.Groups["prefix"].Value}{newValue}{m.Groups["suffix"].Value}";
                },
                // Package ids are compared case-insensitively; culture-aware folding breaks I/i under tr-TR.
                RegexOptions.CultureInvariant);
            matched = found;
            return result;
        }
    }
}
