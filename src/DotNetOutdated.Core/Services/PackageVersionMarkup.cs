using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Text-level edits to package versions and MSBuild properties that keep the rest of the file byte-for-byte. The
    /// elements are found with an XML reader (see <see cref="PackageXml"/>), so comments, CDATA and escaped text are
    /// never mistaken for markup and entity references are decoded. Content that isn't well-formed XML is left
    /// unchanged.
    /// </summary>
    internal static class PackageVersionMarkup
    {
        internal static readonly string[] CentralPackageElements = ["PackageVersion", "GlobalPackageReference"];

        /// <summary>
        /// Replaces the version of every <paramref name="elementNames"/> element whose <c>Include</c> or <c>Update</c>
        /// is <paramref name="packageName"/> (case-insensitive): its <c>Version</c> attribute, or else the text of a
        /// <c>&lt;Version&gt;</c> child element. Only the version text is replaced.
        /// </summary>
        public static string ReplaceVersion(string content, string[] elementNames, string packageName, string newValue) =>
            ReplaceVersion(content, elementNames, packageName, newValue, out _);

        /// <inheritdoc cref="ReplaceVersion(string, string[], string, string)"/>
        /// <param name="matched">True when at least one element matched, even if it already had <paramref name="newValue"/>.</param>
        public static string ReplaceVersion(string content, string[] elementNames, string packageName, string newValue, out bool matched)
        {
            var spans = PackageXml.FindVersionSpans(content, elementNames, packageName);
            matched = spans.Count > 0;
            return ReplaceSpans(content, spans.Select(span => (span.Start, span.Length, PackageXml.Escape(newValue))));
        }

        /// <summary>
        /// Restores a variable reference in <c>Version</c> or <c>VersionOverride</c> attributes or child elements
        /// for <paramref name="packageName"/>. Values that contain a property reference are left untouched unless they
        /// equal <paramref name="previousExpression"/> (the expression being rewritten). When
        /// <paramref name="expectedLiteral"/> is given, literal values are only replaced when they equal it (the version
        /// <c>dotnet add package</c> wrote), so other pins of the package, such as one under another condition, are kept.
        /// </summary>
        public static string ReplacePackageVersion(
            string content,
            string[] elementNames,
            string packageName,
            string newValue,
            string? previousExpression = null,
            VersionRange? expectedLiteral = null)
        {
            var elements = PackageXml.Elements(content);
            if (elements == null)
            {
                return content;
            }

            var escaped = PackageXml.Escape(newValue);
            var edits = new List<(int Start, int Length, string Text)>();
            foreach (var element in elements)
            {
                if (!elementNames.Any(element.Is) || !element.IsPackage(packageName))
                {
                    continue;
                }

                // VersionOverride wins over Version, and a version attribute, even one that isn't restored, takes
                // precedence over child elements.
                if ((element.Attribute("VersionOverride") ?? element.Attribute("Version")) is { } attribute)
                {
                    if (IsRestorable(attribute.Value, previousExpression, expectedLiteral))
                    {
                        edits.Add((attribute.ValueStart, attribute.ValueLength, escaped));
                    }

                    continue;
                }

                foreach (var child in elements.Where(e => e.Parent == element && (e.Is("Version") || e.Is("VersionOverride"))))
                {
                    if (!child.IsEmpty &&
                        IsRestorable(child.Text, previousExpression, expectedLiteral) &&
                        ContentEdit(content, child, escaped) is { } edit)
                    {
                        edits.Add(edit);
                    }
                }
            }

            return ReplaceSpans(content, edits);
        }

        /// <summary>
        /// Replaces the value of the <paramref name="elementIndex"/>-th (zero-based, document order)
        /// <paramref name="propertyName"/> element directly inside a <c>&lt;PropertyGroup&gt;</c>, preserving its
        /// attributes, tag casing and surrounding whitespace. Other definitions of the property are left alone.
        /// </summary>
        public static string ReplacePropertyValueAt(string content, string propertyName, int elementIndex, string newValue)
        {
            if (PropertyAt(content, propertyName, elementIndex) is not { } property)
            {
                return content;
            }

            var escaped = PackageXml.Escape(newValue);
            if (!property.IsEmpty)
            {
                return ContentEdit(content, property, escaped) is { } edit ? ReplaceSpans(content, [edit]) : content;
            }

            // <V /> becomes <V>value</V>, keeping the attributes.
            var openTag = content[property.OpenStart..property.OpenEnd];
            var expanded = $"{openTag[..^2].TrimEnd()}>{escaped}</{property.Name}>";
            return ReplaceSpans(content, [(property.OpenStart, property.OpenEnd - property.OpenStart, expanded)]);
        }

        /// <summary>
        /// Inserts <c>&lt;propertyName&gt;newValue&lt;/propertyName&gt;</c> at the end of the
        /// <paramref name="propertyGroupIndex"/>-th (zero-based, document order, non-empty) <c>&lt;PropertyGroup&gt;</c>,
        /// or, when the index is negative or not found, in a new <c>&lt;PropertyGroup&gt;</c> at the start of
        /// <c>&lt;Project&gt;</c>.
        /// </summary>
        public static string InsertProperty(string content, int propertyGroupIndex, string propertyName, string newValue)
        {
            var elements = PackageXml.Elements(content);
            if (elements == null)
            {
                return content;
            }

            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var property = $"<{propertyName}>{PackageXml.Escape(newValue)}</{propertyName}>";

            if (propertyGroupIndex >= 0 &&
                elements.Where(e => e.Is("PropertyGroup") && !e.IsEmpty).ElementAtOrDefault(propertyGroupIndex) is { } group)
            {
                var body = content[group.OpenEnd..group.CloseStart];
                var lastLf = body.LastIndexOf('\n');
                if (lastLf < 0)
                {
                    // A group on one line stays on one line.
                    return content.Insert(group.CloseStart, property);
                }

                // Insert on a new line before the line holding the end tag, indented like the group's last child line
                // or, without one, one level deeper than the end tag.
                var prefixEnd = lastLf > 0 && body[lastLf - 1] == '\r' ? lastLf - 1 : lastLf;
                var prefix = body[..prefixEnd];
                var closingIndent = body[(lastLf + 1)..];
                var prevLf = prefix.LastIndexOf('\n');
                var prevLine = prevLf >= 0 ? prefix[(prevLf + 1)..] : string.Empty;
                var childIndent = prevLine[..(prevLine.Length - prevLine.AsSpan().TrimStart().Length)];
                if (childIndent.Length == 0)
                {
                    childIndent = closingIndent + (closingIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "  ");
                }

                return content.Insert(group.OpenEnd + prefixEnd, $"{newline}{childIndent}{property}");
            }

            if (elements.FirstOrDefault(e => e.Is("Project")) is not { } project)
            {
                return content;
            }

            var after = project.OpenEnd;
            return after < content.Length && content[after] is '\r' or '\n'
                ? content.Insert(after, $"{newline}  <PropertyGroup>{newline}    {property}{newline}  </PropertyGroup>")
                : content.Insert(after, $"<PropertyGroup>{property}</PropertyGroup>");
        }

        /// <summary>
        /// Inserts <c>&lt;propertyName&gt;newValue&lt;/propertyName&gt;</c> immediately before the
        /// <paramref name="anchorIndex"/>-th <paramref name="anchorName"/> property element (see
        /// <see cref="ReplacePropertyValueAt"/>), on its own line with the same indentation when the anchor starts a line.
        /// Returns <paramref name="content"/> unchanged when the anchor isn't found.
        /// </summary>
        public static string InsertPropertyBefore(string content, string anchorName, int anchorIndex, string propertyName, string newValue)
        {
            if (PropertyAt(content, anchorName, anchorIndex) is not { } anchor)
            {
                return content;
            }

            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var property = $"<{propertyName}>{PackageXml.Escape(newValue)}</{propertyName}>";

            // On its own line when the anchor starts a line inside its group.
            var lineBreak = anchor.OpenStart > 0 ? content.LastIndexOf('\n', anchor.OpenStart - 1) : -1;
            var leading = lineBreak >= 0 ? content[(lineBreak + 1)..anchor.OpenStart] : string.Empty;
            return lineBreak >= anchor.Parent!.OpenEnd && string.IsNullOrWhiteSpace(leading)
                ? content.Insert(anchor.OpenStart, $"{property}{newline}{leading}")
                : content.Insert(anchor.OpenStart, property);
        }

        private static PackageXml.XmlElementSpan? PropertyAt(string content, string propertyName, int index) =>
            index < 0
                ? null
                : PackageXml.Elements(content)?.Where(e => e.IsProperty(propertyName)).ElementAtOrDefault(index);

        // Replaces an element's value, keeping the whitespace around it: blank content is replaced whole, and with
        // comments or other markup in the content only its one text node is replaced. Null when there is more than
        // one text node, so it isn't clear which part is the value.
        private static (int Start, int Length, string Text)? ContentEdit(string content, PackageXml.XmlElementSpan element, string text)
        {
            if (element.TextNodes.Count > 1 || (element.HasMarkup && element.TextNodes.Count == 0))
            {
                return null;
            }

            var (start, length) = element.TextNodes.Count == 1
                ? element.TextNodes[0]
                : (element.OpenEnd, element.CloseStart - element.OpenEnd);
            var end = start + length;
            while (start < end && char.IsWhiteSpace(content[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(content[end - 1]))
            {
                end--;
            }

            return start == end && element.TextNodes.Count == 0
                ? (element.OpenEnd, element.CloseStart - element.OpenEnd, text)
                : (start, end - start, text);
        }

        private static string ReplaceSpans(string content, IEnumerable<(int Start, int Length, string Text)> edits)
        {
            var builder = new StringBuilder(content);
            // Replace from the end so earlier offsets stay valid.
            foreach (var (start, length, text) in edits.OrderByDescending(edit => edit.Start))
            {
                builder.Remove(start, length).Insert(start, text);
            }

            return builder.ToString();
        }


        private static bool IsRestorable(string value, string? previousExpression, VersionRange? expectedLiteral)
        {
            var trimmed = value.Trim();
            if (previousExpression != null && string.Equals(trimmed, previousExpression.Trim(), StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.Contains("$(", StringComparison.Ordinal))
            {
                return false;
            }

            return expectedLiteral == null ||
                   (VersionRange.TryParse(trimmed, out var literal) && literal.Equals(expectedLiteral));
        }
    }
}
