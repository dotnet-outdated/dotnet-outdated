using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Reads MSBuild XML with an XML parser, reporting where each element sits in the original text so that
    /// callers can replace or insert just the text they need and keep the rest of the file byte-for-byte. A full
    /// XDocument save would reserialize the document and lose its layout; regular expressions can't reliably tell
    /// elements from comments, CDATA or escaped text, and don't decode entity references.
    /// </summary>
    internal static class PackageXml
    {
        private static readonly XmlReaderSettings ReaderSettings = new()
        {
            ConformanceLevel = ConformanceLevel.Auto,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        /// <summary>An attribute, with its decoded value and the span of its raw value between the quotes.</summary>
        internal sealed record XmlAttributeSpan(string Name, string Value, int ValueStart, int ValueLength);

        /// <summary>
        /// An element and where it sits in the text: its start tag is <c>[OpenStart, OpenEnd)</c>, its content
        /// <c>[OpenEnd, CloseStart)</c> and its end tag <c>[CloseStart, CloseEnd)</c>. A self-closing element has no
        /// content or end tag, so all three end where its tag does.
        /// </summary>
        internal sealed class XmlElementSpan
        {
            public required string Name { get; init; }
            public required string LocalName { get; init; }
            public XmlElementSpan? Parent { get; init; }
            public required IReadOnlyList<XmlAttributeSpan> Attributes { get; init; }
            public int OpenStart { get; init; }
            public int OpenEnd { get; init; }
            public bool IsEmpty { get; init; }
            public int CloseStart { get; set; }
            public int CloseEnd { get; set; }

            /// <summary>The decoded text directly inside the element.</summary>
            public string Text { get; set; } = string.Empty;

            /// <summary>True when the content holds anything but text: child elements, comments, CDATA or processing instructions.</summary>
            public bool HasMarkup { get; set; }

            /// <summary>The spans of the text nodes directly inside the element, not counting whitespace-only ones.</summary>
            public List<(int Start, int Length)> TextNodes { get; } = [];

            public bool Is(string localName) => LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

            public XmlAttributeSpan? Attribute(string name) =>
                Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            /// <summary>True when its <c>Include</c> or <c>Update</c> is <paramref name="packageName"/> (case-insensitive).</summary>
            public bool IsPackage(string packageName) =>
                Attributes.Any(a =>
                    (a.Name.Equals("Include", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("Update", StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(a.Value.Trim(), packageName, StringComparison.OrdinalIgnoreCase));

            /// <summary>A property definition: an element directly inside a <c>&lt;PropertyGroup&gt;</c>.</summary>
            public bool IsProperty(string propertyName) => Is(propertyName) && Parent?.Is("PropertyGroup") == true;
        }

        /// <summary>
        /// Every element in <paramref name="content"/>, in document order, or null when it isn't well-formed XML.
        /// </summary>
        public static IReadOnlyList<XmlElementSpan>? Elements(string content)
        {
            try
            {
                return ElementsCore(content);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <summary>
        /// The spans of <paramref name="content"/> holding the version of each <paramref name="elementNames"/> element
        /// whose <c>Include</c> or <c>Update</c> is <paramref name="packageName"/> (case-insensitive): the value of its
        /// <c>Version</c> attribute or, without one, the trimmed text of its <c>&lt;Version&gt;</c> child element.
        /// Empty when there are none or the content isn't well-formed XML. Spans are in document order.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length)> FindVersionSpans(string content, string[] elementNames, string packageName)
        {
            var elements = Elements(content);
            if (elements == null)
            {
                return [];
            }

            var spans = new List<(int Start, int Length)>();
            foreach (var element in elements)
            {
                if (!elementNames.Any(element.Is) || !element.IsPackage(packageName))
                {
                    continue;
                }

                if (element.Attribute("Version") is { } version)
                {
                    spans.Add((version.ValueStart, version.ValueLength));
                }
                else if (elements.FirstOrDefault(child => child.Parent == element && child.Is("Version") && TextSpan(content, child) != null) is { } child)
                {
                    spans.Add(TextSpan(content, child)!.Value);
                }
            }

            return spans;
        }

        /// <summary>
        /// The trimmed span of an element's content when it is plain, non-blank text (no comments, CDATA or child
        /// elements); otherwise null.
        /// </summary>
        public static (int Start, int Length)? TextSpan(string content, XmlElementSpan element)
        {
            if (element.IsEmpty || element.HasMarkup)
            {
                return null;
            }

            var start = element.OpenEnd;
            var end = element.CloseStart;
            while (start < end && IsXmlWhitespace(content[start]))
            {
                start++;
            }

            while (end > start && IsXmlWhitespace(content[end - 1]))
            {
                end--;
            }

            return start == end ? null : (start, end - start);
        }

        private static List<XmlElementSpan> ElementsCore(string content)
        {
            // XmlReader skips a leading byte order mark, which would otherwise shift the first line's columns.
            var bomLength = content.StartsWith('﻿') ? 1 : 0;
            var lineStarts = LineStarts(content, bomLength);
            var elements = new List<XmlElementSpan>();
            var open = new Stack<XmlElementSpan>();

            using var reader = XmlReader.Create(new StringReader(content[bomLength..]), ReaderSettings);
            var lineInfo = (IXmlLineInfo)reader;
            int Offset() => lineStarts[lineInfo.LineNumber - 1] + lineInfo.LinePosition - 1;

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        // The reader's position is the element's name, just after '<'.
                        var nameStart = Offset();
                        var name = reader.Name;
                        var localName = reader.LocalName;
                        var isEmpty = reader.IsEmptyElement;
                        var tagEndSearch = nameStart + name.Length;
                        var attributes = new List<XmlAttributeSpan>();
                        while (reader.MoveToNextAttribute())
                        {
                            var (valueStart, valueLength) = AttributeValueSpan(content, Offset());
                            attributes.Add(new XmlAttributeSpan(reader.Name, reader.Value, valueStart, valueLength));
                            tagEndSearch = valueStart + valueLength + 1;
                        }

                        reader.MoveToElement();
                        var openEnd = content.IndexOf('>', tagEndSearch) + 1;
                        var parent = open.Count > 0 ? open.Peek() : null;
                        if (parent != null)
                        {
                            parent.HasMarkup = true;
                        }

                        var element = new XmlElementSpan
                        {
                            Name = name,
                            LocalName = localName,
                            Parent = parent,
                            Attributes = attributes,
                            OpenStart = nameStart - 1,
                            OpenEnd = openEnd,
                            IsEmpty = isEmpty,
                            CloseStart = openEnd,
                            CloseEnd = openEnd,
                        };
                        elements.Add(element);
                        if (!isEmpty)
                        {
                            open.Push(element);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                    {
                        // The reader's position is the element's name, just after "</".
                        var element = open.Pop();
                        var nameStart = Offset();
                        element.CloseStart = nameStart - 2;
                        element.CloseEnd = content.IndexOf('>', nameStart) + 1;
                        break;
                    }

                    case XmlNodeType.Text:
                        if (open.Count > 0)
                        {
                            // Text runs to the next markup, which always starts with '<'.
                            var textStart = Offset();
                            var textEnd = content.IndexOf('<', textStart);
                            open.Peek().TextNodes.Add((textStart, (textEnd < 0 ? content.Length : textEnd) - textStart));
                            open.Peek().Text += reader.Value;
                        }

                        break;

                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (open.Count > 0)
                        {
                            open.Peek().Text += reader.Value;
                        }

                        break;

                    case XmlNodeType.Comment:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.ProcessingInstruction:
                    case XmlNodeType.EntityReference:
                        if (open.Count > 0)
                        {
                            open.Peek().HasMarkup = true;
                        }

                        break;
                }
            }

            return elements;
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
