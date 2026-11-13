using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class PackageVersionMarkupTests
    {
        private static readonly string[] PackageReference = ["PackageReference"];

        // ── ReplaceVersion ────────────────────────────────────────────────────────

        [Theory]
        // Element names are matched case-insensitively.
        [InlineData(
            "<packagereference Include=\"A\" Version=\"1.0.0\" />",
            "<packagereference Include=\"A\" Version=\"2.0.0\" />")]
        // Only the Version attribute is rewritten, whatever attributes come before it.
        [InlineData(
            "<PackageReference Include=\"A\" Condition=\"'$(X)' == 'y'\" PrivateAssets=\"all\" Version=\"1.0.0\" />",
            "<PackageReference Include=\"A\" Condition=\"'$(X)' == 'y'\" PrivateAssets=\"all\" Version=\"2.0.0\" />")]
        // A plain version update ignores VersionOverride.
        [InlineData(
            "<PackageReference Include=\"A\" Version=\"1.0.0\" VersionOverride=\"1.5.0\" />",
            "<PackageReference Include=\"A\" Version=\"2.0.0\" VersionOverride=\"1.5.0\" />")]
        [InlineData(
            "<PackageReference Include=\"A\" VersionOverride=\"1.5.0\" Version=\"1.0.0\" />",
            "<PackageReference Include=\"A\" VersionOverride=\"1.5.0\" Version=\"2.0.0\" />")]
        // A quoted value may span lines.
        [InlineData(
            "<PackageReference Include=\"A\" Version=\"\n  1.0.0\n\" />",
            "<PackageReference Include=\"A\" Version=\"2.0.0\" />")]
        public void ReplaceVersion_RewritesOnlyTheVersionAttribute(string content, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.ReplaceVersion(content, PackageReference, "A", "2.0.0", out var matched));
            Assert.True(matched);
        }

        [Theory]
        [InlineData("<PackageReference Include=\"A\" />")]
        [InlineData("<PackageReference Include=\"A\" PrivateAssets=\"all\" />")]
        [InlineData("<PackageReference Include=\"A\" VersionOverride=\"1.0.0\" />")]
        public void ReplaceVersion_NoVersionAttribute_LeavesElementAlone(string content)
        {
            Assert.Equal(content, PackageVersionMarkup.ReplaceVersion(content, PackageReference, "A", "2.0.0", out var matched));
            Assert.False(matched);
        }

        // MSBuild accepts item metadata as child elements as well as attributes, so a version can be written as
        // <Version>; only its text is replaced, keeping the surrounding whitespace.
        [Theory]
        [InlineData(
            "<PackageReference Include=\"A\"><Version>1.0.0</Version></PackageReference>",
            "<PackageReference Include=\"A\"><Version>2.0.0</Version></PackageReference>")]
        [InlineData(
            "<PackageReference Include=\"A\">\n  <PrivateAssets>all</PrivateAssets>\n  <Version>\n    1.0.0\n  </Version>\n</PackageReference>",
            "<PackageReference Include=\"A\">\n  <PrivateAssets>all</PrivateAssets>\n  <Version>\n    2.0.0\n  </Version>\n</PackageReference>")]
        public void ReplaceVersion_VersionChildElement_RewritesItsText(string content, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.ReplaceVersion(content, PackageReference, "A", "2.0.0", out var matched));
            Assert.True(matched);
        }

        // The package id is compared after XML entity references are decoded, as MSBuild does.
        [Fact]
        public void ReplaceVersion_EntityInPackageId_MatchesTheDecodedId()
        {
            const string content = "<PackageReference Include=\"A&#46;B\" Version=\"1.0.0\" />";

            Assert.Equal(
                "<PackageReference Include=\"A&#46;B\" Version=\"2.0.0\" />",
                PackageVersionMarkup.ReplaceVersion(content, PackageReference, "A.B", "2.0.0", out var matched));
            Assert.True(matched);
        }

        // The new value is escaped, so characters special to XML can't break the file.
        [Fact]
        public void ReplaceVersion_EscapesTheNewValue()
        {
            Assert.Equal(
                "<PackageReference Include=\"A\" Version=\"&amp;&lt;&gt;&quot;&apos;\" />",
                PackageVersionMarkup.ReplaceVersion("<PackageReference Include=\"A\" Version=\"1.0.0\" />", PackageReference, "A", "&<>\"'"));
        }

        // Markup that only looks like an element (inside a comment, CDATA or a property value) is never edited.
        [Theory]
        [InlineData("<!-- <PackageReference Include=\"A\" Version=\"1.0.0\" /> -->")]
        [InlineData("<P><![CDATA[<PackageReference Include=\"A\" Version=\"1.0.0\" />]]></P>")]
        [InlineData("<P>&lt;PackageReference Include=\"A\" Version=\"1.0.0\" /&gt;</P>")]
        public void ReplaceVersion_MarkupThatIsNotAnElement_IsLeftAlone(string content)
        {
            Assert.Equal(content, PackageVersionMarkup.ReplaceVersion(content, PackageReference, "A", "2.0.0", out var matched));
            Assert.False(matched);
        }

        // ── ReplacePackageVersion ─────────────────────────────────────────────────

        [Theory]
        // VersionOverride wins over Version when restoring.
        [InlineData(
            "<PackageReference Include=\"A\" Version=\"1.0.0\" VersionOverride=\"1.0.0\" />",
            "<PackageReference Include=\"A\" Version=\"1.0.0\" VersionOverride=\"$(V)\" />")]
        // Other attributes are never mistaken for the version.
        [InlineData(
            "<PackageReference Include=\"A\" Version=\"1.0.0\" PrivateAssets=\"all\" />",
            "<PackageReference Include=\"A\" Version=\"$(V)\" PrivateAssets=\"all\" />")]
        [InlineData(
            "<PackageReference Include=\"A\" VersionOverride=\"1.0.0\" PrivateAssets=\"all\" />",
            "<PackageReference Include=\"A\" VersionOverride=\"$(V)\" PrivateAssets=\"all\" />")]
        // A value referencing another property is left alone.
        [InlineData(
            "<PackageReference Include=\"A\" Version=\"$(Other)\" />",
            "<PackageReference Include=\"A\" Version=\"$(Other)\" />")]
        // Child Version / VersionOverride elements, any casing, keeping surrounding whitespace.
        [InlineData(
            "<PackageReference Include=\"A\">\n  <Version> 1.0.0 </Version>\n</PackageReference>",
            "<PackageReference Include=\"A\">\n  <Version> $(V) </Version>\n</PackageReference>")]
        [InlineData(
            "<packagereference Include=\"A\"><version>1.0.0</version></packagereference>",
            "<packagereference Include=\"A\"><version>$(V)</version></packagereference>")]
        [InlineData(
            "<PackageReference Include=\"A\"><VersionOverride>1.0.0</VersionOverride></PackageReference>",
            "<PackageReference Include=\"A\"><VersionOverride>$(V)</VersionOverride></PackageReference>")]
        // Comments in the body and child values referencing another property are kept.
        [InlineData(
            "<PackageReference Include=\"A\"><!-- pinned --><Version>$(Other)</Version></PackageReference>",
            "<PackageReference Include=\"A\"><!-- pinned --><Version>$(Other)</Version></PackageReference>")]
        // A version attribute, even one that isn't restored, takes precedence over child elements.
        [InlineData(
            "<PackageReference Include=\"A\" VersionOverride=\"$(Other)\"><Version>1.0.0</Version></PackageReference>",
            "<PackageReference Include=\"A\" VersionOverride=\"$(Other)\"><Version>1.0.0</Version></PackageReference>")]
        // Child version elements without a version attribute are restored, whatever other attributes there are.
        [InlineData(
            "<PackageReference Include=\"A\" PrivateAssets=\"all\"><Version>1.0.0</Version></PackageReference>",
            "<PackageReference Include=\"A\" PrivateAssets=\"all\"><Version>$(V)</Version></PackageReference>")]
        [InlineData(
            "<PackageReference Include=\"B\"><Version>1.0.0</Version></PackageReference>",
            "<PackageReference Include=\"B\"><Version>1.0.0</Version></PackageReference>")]
        public void ReplacePackageVersion_RestoresTheVariable(string content, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.ReplacePackageVersion(content, PackageReference, "A", "$(V)"));
        }

        [Fact]
        public void ReplacePackageVersion_PreviousExpression_IsRewrittenEvenThoughItHasAVariable()
        {
            const string content = "<PackageReference Include=\"A\" Version=\"[$(V), 2.0.0)\" />";

            Assert.Equal(
                "<PackageReference Include=\"A\" Version=\"[$(V), 3.0.0)\" />",
                PackageVersionMarkup.ReplacePackageVersion(content, PackageReference, "A", "[$(V), 3.0.0)", "[$(V), 2.0.0)"));
        }

        [Fact]
        public void ReplacePackageVersion_ExpectedLiteral_KeepsOtherPins()
        {
            const string content =
                "<PackageReference Include=\"A\" Version=\"1.0.0\" />\n" +
                "<PackageReference Include=\"A\" Version=\"1.5.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />";

            Assert.Equal(
                "<PackageReference Include=\"A\" Version=\"$(V)\" />\n" +
                "<PackageReference Include=\"A\" Version=\"1.5.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />",
                PackageVersionMarkup.ReplacePackageVersion(content, PackageReference, "A", "$(V)", expectedLiteral: VersionRange.Parse("1.0.0")));
        }

        [Fact]
        public void ReplacePackageVersion_SeveralElementNames_RestoresEach()
        {
            const string content =
                "<PackageVersion Include=\"A\"><Version>1.0.0</Version></PackageVersion>\n" +
                "<GlobalPackageReference Include=\"A\" Version=\"1.0.0\" />";

            Assert.Equal(
                "<PackageVersion Include=\"A\"><Version>$(V)</Version></PackageVersion>\n" +
                "<GlobalPackageReference Include=\"A\" Version=\"$(V)\" />",
                PackageVersionMarkup.ReplacePackageVersion(content, PackageVersionMarkup.CentralPackageElements, "A", "$(V)"));
        }

        // MSBuild decodes XML entity references in a package id, but the raw text is compared, so it doesn't match.
        [Fact]
        public void ReplacePackageVersion_EntityInPackageId_DoesNotMatchTheDecodedId()
        {
            Assert.Equal(
                "<PackageReference Include=\"A&#46;B\" Version=\"1.0.0\" />",
                PackageVersionMarkup.ReplacePackageVersion("<PackageReference Include=\"A&#46;B\" Version=\"1.0.0\" />", PackageReference, "A.B", "$(V)"));
        }

        // ── ReplacePropertyValueAt ────────────────────────────────────────────────

        [Theory]
        // Whitespace around the value is kept.
        [InlineData("<PropertyGroup><V> 1.0.0 </V></PropertyGroup>", 0, "<PropertyGroup><V> 2.0.0 </V></PropertyGroup>")]
        [InlineData("<PropertyGroup><V>\n    1.0.0\n  </V></PropertyGroup>", 0, "<PropertyGroup><V>\n    2.0.0\n  </V></PropertyGroup>")]
        // A blank or self-closing definition gets the value.
        [InlineData("<PropertyGroup><V>  </V></PropertyGroup>", 0, "<PropertyGroup><V>2.0.0</V></PropertyGroup>")]
        [InlineData("<PropertyGroup><V Condition=\"'a' == 'a'\" /></PropertyGroup>", 0, "<PropertyGroup><V Condition=\"'a' == 'a'\">2.0.0</V></PropertyGroup>")]
        // Property and PropertyGroup names are case-insensitive; the original casing is kept.
        [InlineData("<propertygroup><v>1.0.0</v></propertygroup>", 0, "<propertygroup><v>2.0.0</v></propertygroup>")]
        // Only the indexed definition changes; commented-out definitions are not counted.
        [InlineData(
            "<PropertyGroup><!-- <V>0.1</V> --><V>1.0.0</V></PropertyGroup><PropertyGroup><V/></PropertyGroup>",
            1,
            "<PropertyGroup><!-- <V>0.1</V> --><V>1.0.0</V></PropertyGroup><PropertyGroup><V>2.0.0</V></PropertyGroup>")]
        public void ReplacePropertyValueAt_ReplacesTheIndexedDefinition(string content, int index, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.ReplacePropertyValueAt(content, "V", index, "2.0.0"));
        }

        // A closing tag inside a comment ends the group early, so the definition after it isn't found.
        [Fact]
        public void ReplacePropertyValueAt_ClosingTagInAComment_DoesNotFindTheDefinition()
        {
            Assert.Equal(
                "<PropertyGroup><!-- </PropertyGroup> --><V>1.0.0</V></PropertyGroup>",
                PackageVersionMarkup.ReplacePropertyValueAt("<PropertyGroup><!-- </PropertyGroup> --><V>1.0.0</V></PropertyGroup>", "V", 0, "2.0.0"));
        }

        // ── InsertProperty ────────────────────────────────────────────────────────

        [Theory]
        // Follows the indentation of the group's last line.
        [InlineData(
            "<Project>\n  <PropertyGroup>\n    <A>1</A>\n  </PropertyGroup>\n</Project>",
            0,
            "<Project>\n  <PropertyGroup>\n    <A>1</A>\n    <V>2.0.0</V>\n  </PropertyGroup>\n</Project>")]
        [InlineData(
            "<Project>\n  <PropertyGroup>\n        <A>1</A>\n    <B>1</B>\n  </PropertyGroup>\n</Project>",
            0,
            "<Project>\n  <PropertyGroup>\n        <A>1</A>\n    <B>1</B>\n    <V>2.0.0</V>\n  </PropertyGroup>\n</Project>")]
        [InlineData(
            "<Project>\n<PropertyGroup>\n\t<A>1</A>\n</PropertyGroup>\n</Project>",
            0,
            "<Project>\n<PropertyGroup>\n\t<A>1</A>\n\t<V>2.0.0</V>\n</PropertyGroup>\n</Project>")]
        // Keeps CRLF line endings.
        [InlineData(
            "<Project>\r\n  <PropertyGroup>\r\n    <A>1</A>\r\n  </PropertyGroup>\r\n</Project>",
            0,
            "<Project>\r\n  <PropertyGroup>\r\n    <A>1</A>\r\n    <V>2.0.0</V>\r\n  </PropertyGroup>\r\n</Project>")]
        // No indented child line to copy: one level deeper than the closing tag, with spaces or a tab.
        [InlineData(
            "<Project>\n  <PropertyGroup><A>1</A>\n  </PropertyGroup>\n</Project>",
            0,
            "<Project>\n  <PropertyGroup><A>1</A>\n    <V>2.0.0</V>\n  </PropertyGroup>\n</Project>")]
        [InlineData(
            "<Project>\n\t<PropertyGroup><A>1</A>\n\t</PropertyGroup>\n</Project>",
            0,
            "<Project>\n\t<PropertyGroup><A>1</A>\n\t\t<V>2.0.0</V>\n\t</PropertyGroup>\n</Project>")]
        [InlineData(
            "<Project><PropertyGroup>\n</PropertyGroup></Project>",
            0,
            "<Project><PropertyGroup>\n  <V>2.0.0</V>\n</PropertyGroup></Project>")]
        // A group on one line stays on one line.
        [InlineData(
            "<Project><PropertyGroup><A>1</A></PropertyGroup></Project>",
            0,
            "<Project><PropertyGroup><A>1</A><V>2.0.0</V></PropertyGroup></Project>")]
        // Groups are counted in document order, case-insensitively.
        [InlineData(
            "<Project><propertygroup><A>1</A></propertygroup><PropertyGroup><B>1</B></PropertyGroup></Project>",
            1,
            "<Project><propertygroup><A>1</A></propertygroup><PropertyGroup><B>1</B><V>2.0.0</V></PropertyGroup></Project>")]
        public void InsertProperty_IntoExistingGroup(string content, int groupIndex, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.InsertProperty(content, groupIndex, "V", "2.0.0"));
        }

        [Theory]
        [InlineData(
            "<Project>\n</Project>",
            "<Project>\n  <PropertyGroup>\n    <V>2.0.0</V>\n  </PropertyGroup>\n</Project>")]
        [InlineData(
            "<Project>\r\n</Project>",
            "<Project>\r\n  <PropertyGroup>\r\n    <V>2.0.0</V>\r\n  </PropertyGroup>\r\n</Project>")]
        [InlineData(
            "<Project></Project>",
            "<Project><PropertyGroup><V>2.0.0</V></PropertyGroup></Project>")]
        // The XML declaration and comments before <Project> are skipped; the tag is matched case-insensitively.
        [InlineData(
            "<?xml version=\"1.0\"?>\n<!-- <Project> -->\n<project Sdk=\"Microsoft.NET.Sdk\">\n</project>",
            "<?xml version=\"1.0\"?>\n<!-- <Project> -->\n<project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <V>2.0.0</V>\n  </PropertyGroup>\n</project>")]
        public void InsertProperty_NewGroupAtStartOfProject(string content, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.InsertProperty(content, -1, "V", "2.0.0"));
        }

        [Fact]
        public void InsertProperty_GroupIndexNotFound_AddsNewGroup()
        {
            Assert.Equal(
                "<Project><PropertyGroup><V>2.0.0</V></PropertyGroup><PropertyGroup><A>1</A></PropertyGroup></Project>",
                PackageVersionMarkup.InsertProperty("<Project><PropertyGroup><A>1</A></PropertyGroup></Project>", 1, "V", "2.0.0"));
        }

        // A closing tag inside a comment ends the group early, so the property is inserted inside the comment.
        [Fact]
        public void InsertProperty_ClosingTagInAComment_InsertsInsideTheComment()
        {
            Assert.Equal(
                "<Project><PropertyGroup><!-- <V>2.0.0</V></PropertyGroup> --><A>1</A></PropertyGroup></Project>",
                PackageVersionMarkup.InsertProperty("<Project><PropertyGroup><!-- </PropertyGroup> --><A>1</A></PropertyGroup></Project>", 0, "V", "2.0.0"));
        }

        // ── InsertPropertyBefore ──────────────────────────────────────────────────

        [Theory]
        // On its own line, with the anchor's indentation and line ending.
        [InlineData(
            "<PropertyGroup>\n    <A>$(V)</A>\n</PropertyGroup>",
            0,
            "<PropertyGroup>\n    <V>2.0.0</V>\n    <A>$(V)</A>\n</PropertyGroup>")]
        [InlineData(
            "<PropertyGroup>\r\n    <A>$(V)</A>\r\n</PropertyGroup>",
            0,
            "<PropertyGroup>\r\n    <V>2.0.0</V>\r\n    <A>$(V)</A>\r\n</PropertyGroup>")]
        // Inline when the anchor doesn't start its line.
        [InlineData(
            "<PropertyGroup><A>$(V)</A></PropertyGroup>",
            0,
            "<PropertyGroup><V>2.0.0</V><A>$(V)</A></PropertyGroup>")]
        [InlineData(
            "<PropertyGroup>\n    <B>1</B><A>$(V)</A>\n</PropertyGroup>",
            0,
            "<PropertyGroup>\n    <B>1</B><V>2.0.0</V><A>$(V)</A>\n</PropertyGroup>")]
        // Only before the indexed anchor; commented-out anchors are not counted; self-closing anchors are.
        [InlineData(
            "<PropertyGroup><!-- <A>x</A> --><A>1</A><A /><A>$(V)</A></PropertyGroup>",
            2,
            "<PropertyGroup><!-- <A>x</A> --><A>1</A><A /><V>2.0.0</V><A>$(V)</A></PropertyGroup>")]
        [InlineData(
            "<PropertyGroup><A>1</A><A /></PropertyGroup>",
            1,
            "<PropertyGroup><A>1</A><V>2.0.0</V><A /></PropertyGroup>")]
        // Anchor not found: unchanged.
        [InlineData(
            "<PropertyGroup><A>1</A></PropertyGroup>",
            1,
            "<PropertyGroup><A>1</A></PropertyGroup>")]
        public void InsertPropertyBefore_InsertsBeforeTheAnchor(string content, int anchorIndex, string expected)
        {
            Assert.Equal(expected, PackageVersionMarkup.InsertPropertyBefore(content, "A", anchorIndex, "V", "2.0.0"));
        }

        // A closing tag inside a comment ends the group early, so the anchor after it isn't found.
        [Fact]
        public void InsertPropertyBefore_ClosingTagInAComment_DoesNotFindTheAnchor()
        {
            Assert.Equal(
                "<PropertyGroup><!-- </PropertyGroup> --><A>$(V)</A></PropertyGroup>",
                PackageVersionMarkup.InsertPropertyBefore("<PropertyGroup><!-- </PropertyGroup> --><A>$(V)</A></PropertyGroup>", "A", 0, "V", "2.0.0"));
        }
    }
}
