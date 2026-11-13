using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Finds package version text in MSBuild XML with an XML parser, reporting where it sits in the original text
    /// so that callers can replace just that text and keep the rest of the file byte-for-byte. A full XDocument save
    /// would reserialize the document and lose its layout; regular expressions can't reliably tell elements from
    /// comments, CDATA or escaped text, and don't decode entity references.
    /// </summary>
    internal static class PackageXml
    {
        private static readonly XmlReaderSettings ReaderSettings = new()
        {
            ConformanceLevel = ConformanceLevel.Auto,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        /// <summary>
        /// The spans of <paramref name="content"/> holding the version of each <paramref name="elementNames"/> element
        /// whose <c>Include</c> or <c>Update</c> is <paramref name="packageName"/> (case-insensitive): the value of its
        /// <c>Version</c> attribute or, without one, the trimmed text of its <c>&lt;Version&gt;</c> child element.
        /// Empty when there are none or the content isn't well-formed XML. Spans are in document order.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length)> FindVersionSpans(string content, string[] elementNames, string packageName)
        {
            try
            {
                return FindVersionSpansCore(content, elementNames, packageName);
            }
            catch (XmlException)
            {
                return [];
            }
        }

        private static List<(int Start, int Length)> FindVersionSpansCore(string content, string[] elementNames, string packageName)
        {
            // XmlReader skips a leading byte order mark, which would otherwise shift the first line's columns.
            var bomLength = content.StartsWith('﻿') ? 1 : 0;
            var lineStarts = LineStarts(content, bomLength);
            var spans = new List<(int Start, int Length)>();

            using var reader = XmlReader.Create(new StringReader(content[bomLength..]), ReaderSettings);
            var lineInfo = (IXmlLineInfo)reader;
            int Offset() => lineStarts[lineInfo.LineNumber - 1] + lineInfo.LinePosition - 1;

            // Depth of a matched element with no Version attribute, whose <Version> child is being looked for.
            var pendingDepth = -1;

            while (reader.Read())
            {
                if (pendingDepth >= 0)
                {
                    if (reader.Depth <= pendingDepth)
                    {
                        pendingDepth = -1;
                    }
                    else
                    {
                        if (reader.Depth == pendingDepth + 1 &&
                            reader.NodeType == XmlNodeType.Element &&
                            reader.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase) &&
                            !reader.IsEmptyElement &&
                            TryReadTextSpan(reader, content, Offset) is { } textSpan)
                        {
                            spans.Add(textSpan);
                            pendingDepth = -1;
                        }

                        continue;
                    }
                }

                if (reader.NodeType != XmlNodeType.Element ||
                    !elementNames.Contains(reader.LocalName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isPackage = false;
                (int Start, int Length)? versionSpan = null;
                while (reader.MoveToNextAttribute())
                {
                    if (reader.Name.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                        reader.Name.Equals("Update", StringComparison.OrdinalIgnoreCase))
                    {
                        isPackage |= string.Equals(reader.Value.Trim(), packageName, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (reader.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) && versionSpan == null)
                    {
                        versionSpan = AttributeValueSpan(content, Offset());
                    }
                }

                reader.MoveToElement();
                if (!isPackage)
                {
                    continue;
                }

                if (versionSpan is { } span)
                {
                    spans.Add(span);
                }
                else if (!reader.IsEmptyElement)
                {
                    pendingDepth = reader.Depth;
                }
            }

            return spans;
        }

        // The value between the quotes of the attribute whose name starts at nameOffset.
        private static (int Start, int Length) AttributeValueSpan(string content, int nameOffset)
        {
            var equals = content.IndexOf('=', nameOffset);
            var quoteIndex = content.IndexOfAny(['"', '\''], equals + 1);
            var start = quoteIndex + 1;
            var end = content.IndexOf(content[quoteIndex], start);
            return (start, end - start);
        }

        // Reads a <Version> element's content; returns its trimmed span when it is plain text (no comments, CDATA or
        // child elements), leaving the reader on its end tag.
        private static (int Start, int Length)? TryReadTextSpan(XmlReader reader, string content, Func<int> offset)
        {
            if (!reader.Read() || reader.NodeType != XmlNodeType.Text)
            {
                return null;
            }

            var start = offset();
            var end = content.IndexOf('<', start);
            if (!reader.Read() || reader.NodeType != XmlNodeType.EndElement)
            {
                return null;
            }

            while (start < end && IsXmlWhitespace(content[start]))
            {
                start++;
            }

            while (end > start && IsXmlWhitespace(content[end - 1]))
            {
                end--;
            }

            return (start, end - start);
        }

        /// <summary>
        /// Escapes <paramref name="value"/> for writing into the raw text of an XML file, either between an attribute's
        /// quotes (of either kind) or as an element's text: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c> and
        /// <c>'</c> become entity references.
        /// </summary>
        public static string Escape(string value) =>
            value.Replace("&", "&amp;", StringComparison.Ordinal)
                 .Replace("<", "&lt;", StringComparison.Ordinal)
                 .Replace(">", "&gt;", StringComparison.Ordinal)
                 .Replace("\"", "&quot;", StringComparison.Ordinal)
                 .Replace("'", "&apos;", StringComparison.Ordinal);

        private static bool IsXmlWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

        // The offset where each line starts, counting "\r\n", "\r" and "\n" as line breaks as XmlReader does.
        private static List<int> LineStarts(string content, int firstLineStart)
        {
            var starts = new List<int> { firstLineStart };
            for (var i = firstLineStart; i < content.Length; i++)
            {
                if (content[i] == '\r')
                {
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i++;
                    }

                    starts.Add(i + 1);
                }
                else if (content[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts;
        }
    }
}
