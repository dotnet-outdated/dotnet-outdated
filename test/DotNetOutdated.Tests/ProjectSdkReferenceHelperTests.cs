using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Text;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class ProjectSdkReferenceHelperTests
    {
        private static readonly string ProjectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");

        private static MockFileSystem CreateFileSystem(string projectContent) =>
            new(new Dictionary<string, MockFileData>
            {
                { ProjectPath, new MockFileData(projectContent) }
            });

        [Fact]
        public void Discover_ProjectSdkAttribute_ReturnsVersionedSdk()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.0""></Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));

            Assert.Equal("Aspire.AppHost.Sdk", reference.Name);
            Assert.Equal(new NuGetVersion("13.0.0"), reference.ResolvedVersion);
            Assert.Equal(new NuGetVersion("13.0.0"), reference.VersionRange.MinVersion);
            Assert.True(reference.VersionRange.IsMinInclusive);
            Assert.Null(reference.VersionRange.MaxVersion);
        }

        [Fact]
        public void Discover_SdkElement_ReturnsVersionedSdk()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""9.5.0"" />
</Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));

            Assert.Equal("Aspire.AppHost.Sdk", reference.Name);
            Assert.Equal(new NuGetVersion("9.5.0"), reference.ResolvedVersion);
        }

        [Fact]
        public void Discover_MultipleSdksInProjectAttribute_ReturnsOnlyVersionedSdks()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.NET.Sdk; Microsoft.Build.NoTargets/3.7.0""></Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));

            Assert.Equal("Microsoft.Build.NoTargets", reference.Name);
            Assert.Equal(new NuGetVersion("3.7.0"), reference.ResolvedVersion);
        }

        [Fact]
        public void Discover_IgnoresUnversionedAndPropertyBackedSdks()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <Sdk Name=""Aspire.AppHost.Sdk"" />
  <Sdk Name=""Microsoft.Build.NoTargets"" Version=""$(NoTargetsVersion)"" />
  <Sdk Name=""$(SdkName)"" Version=""1.0.0"" />
</Project>");

            Assert.Empty(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
        }

        [Fact]
        public void Discover_MissingProjectFile_ReturnsEmpty()
        {
            var fileSystem = new MockFileSystem();

            Assert.Empty(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
        }

        [Fact]
        public void Discover_IgnoresSdksInComments()
        {
            var fileSystem = CreateFileSystem(@"<!-- was: <Project Sdk=""Microsoft.Build.NoTargets/1.0.0""> -->
<Project Sdk=""Microsoft.Build.NoTargets/3.7.0"">
  <!-- Old setup, kept for reference:
  <Sdk Name=""Microsoft.Build.Traversal"" Version=""3.0.0"" />
  -->
</Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));

            Assert.Equal("Microsoft.Build.NoTargets", reference.Name);
            Assert.Equal(new NuGetVersion("3.7.0"), reference.ResolvedVersion);
        }

        [Fact]
        public void Discover_MinimumVersionOnly_IsNotReported()
        {
            var fileSystem = CreateFileSystem(@"<Project><Sdk Name=""Microsoft.Build.NoTargets"" MinimumVersion=""3.7.0"" /></Project>");

            Assert.Empty(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
        }

        [Fact]
        public void Discover_InvalidXml_ReturnsEmpty()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.0"">");

            Assert.Empty(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
        }

        [Fact]
        public void GetDeclarations_ReturnsEveryFormInDocumentOrder()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.NET.Sdk; Microsoft.Build.NoTargets/3.7.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <!-- <Sdk Name=""Commented.Sdk"" Version=""1.0.0"" /> -->
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""$(AspireVersion)"" />
  <ImportGroup>
    <Import Project=""Sdk.props"" Sdk=""MSTest.Sdk"" Version=""3.6.0"" />
  </ImportGroup>
  <Import Project=""Directory.Build.targets"" />
</Project>");

            var declarations = ProjectSdkReferenceHelper.GetDeclarations(fileSystem, ProjectPath);

            Assert.Equal(
                [
                    new ProjectSdkDeclaration("Microsoft.NET.Sdk", null, ProjectSdkDeclarationKind.ProjectAttribute),
                    new ProjectSdkDeclaration("Microsoft.Build.NoTargets", "3.7.0", ProjectSdkDeclarationKind.ProjectAttribute),
                    new ProjectSdkDeclaration("Aspire.AppHost.Sdk", "$(AspireVersion)", ProjectSdkDeclarationKind.SdkElement),
                    new ProjectSdkDeclaration("MSTest.Sdk", "3.6.0", ProjectSdkDeclarationKind.ImportElement)
                ],
                declarations);
        }

        [Fact]
        public void GetDeclarations_MissingOrInvalidFile_ReturnsEmpty()
        {
            Assert.Empty(ProjectSdkReferenceHelper.GetDeclarations(new MockFileSystem(), ProjectPath));
            Assert.Empty(ProjectSdkReferenceHelper.GetDeclarations(CreateFileSystem("not xml"), ProjectPath));
        }

        [Fact]
        public void GetDeclarations_RootIsNotProject_ReturnsEmpty()
        {
            var fileSystem = CreateFileSystem(@"<Sdk Name=""X.Sdk"" Version=""1.0.0"" Sdk=""Y.Sdk/1.0.0""><Sdk Name=""Z.Sdk"" Version=""1.0.0"" /></Sdk>");

            Assert.Empty(ProjectSdkReferenceHelper.GetDeclarations(fileSystem, ProjectPath));
        }

        [Fact]
        public void GetDeclarations_ProjectSdkWithoutName_IsSkipped()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""/1.0.0; ;X.Sdk/2.0.0""></Project>");

            var declaration = Assert.Single(ProjectSdkReferenceHelper.GetDeclarations(fileSystem, ProjectPath));
            Assert.Equal(new ProjectSdkDeclaration("X.Sdk", "2.0.0", ProjectSdkDeclarationKind.ProjectAttribute), declaration);
        }

        [Fact]
        public void TryUpdate_ProjectSdkAttribute_UpdatesOnlyMatchingSdk()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk='Microsoft.NET.Sdk;Aspire.AppHost.Sdk/13.0.0'>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var updated = ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "aspire.apphost.sdk", new NuGetVersion("13.0.2"));

            Assert.True(updated);
            Assert.Equal(@"<Project Sdk='Microsoft.NET.Sdk;Aspire.AppHost.Sdk/13.0.2'>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_SdkElement_PreservesOtherAttributesAndPackageVersions()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""9.5.0"" />
  <ItemGroup>
    <PackageReference Include=""Aspire.Hosting.AppHost"" Version=""9.5.0"" />
  </ItemGroup>
</Project>");

            var updated = ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Aspire.AppHost.Sdk", new NuGetVersion("9.5.2"));

            Assert.True(updated);
            var content = fileSystem.File.ReadAllText(ProjectPath);
            Assert.Contains(@"<Sdk Name=""Aspire.AppHost.Sdk"" Version=""9.5.2"" />", content);
            Assert.Contains(@"<PackageReference Include=""Aspire.Hosting.AppHost"" Version=""9.5.0"" />", content);
        }

        [Fact]
        public void TryUpdate_LeavesSdksInCommentsAndCDataUnchanged()
        {
            var fileSystem = CreateFileSystem(@"<!-- was: <Project Sdk=""Microsoft.Build.NoTargets/1.0.0""> -->
<Project Sdk=""Microsoft.Build.NoTargets/3.7.0"">
  <!-- <Sdk Name=""Microsoft.Build.NoTargets"" Version=""3.0.0"" /> -->
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""9.5.0"" />
  <ProjectExtensions><![CDATA[ <Sdk Name=""Microsoft.Build.NoTargets"" Version=""2.0.0"" /> ]]></ProjectExtensions>
</Project>");

            var updated = ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Microsoft.Build.NoTargets", new NuGetVersion("3.7.134"));

            Assert.True(updated);
            Assert.Equal(@"<!-- was: <Project Sdk=""Microsoft.Build.NoTargets/1.0.0""> -->
<Project Sdk=""Microsoft.Build.NoTargets/3.7.134"">
  <!-- <Sdk Name=""Microsoft.Build.NoTargets"" Version=""3.0.0"" /> -->
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""9.5.0"" />
  <ProjectExtensions><![CDATA[ <Sdk Name=""Microsoft.Build.NoTargets"" Version=""2.0.0"" /> ]]></ProjectExtensions>
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_CommentedOutSdkOnly_ReturnsFalse()
        {
            const string content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- <Sdk Name=""Microsoft.Build.Traversal"" Version=""3.0.0"" /> -->
</Project>";
            var fileSystem = CreateFileSystem(content);

            Assert.False(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Microsoft.Build.Traversal", new NuGetVersion("4.1.82")));
            Assert.Equal(content, fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_SdkElementWithVersionBeforeName_UpdatesVersion()
        {
            var fileSystem = CreateFileSystem(@"<Project><Sdk Version=""1.0.0"" Name=""X.Sdk"" /></Project>");

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(@"<Project><Sdk Version=""2.0.0"" Name=""X.Sdk"" /></Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void Discover_And_TryUpdate_ImportSdkElements()
        {
            var fileSystem = CreateFileSystem(@"<Project>
  <Import Project=""Sdk.props"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.0"" />
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <Import Project=""Sdk.targets"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.0"" />
  <Import Project=""Directory.Build.targets"" />
</Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
            Assert.Equal("Microsoft.Build.NoTargets", reference.Name);
            Assert.Equal(new NuGetVersion("3.7.0"), reference.ResolvedVersion);

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Microsoft.Build.NoTargets", new NuGetVersion("3.7.134")));
            Assert.Equal(@"<Project>
  <Import Project=""Sdk.props"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.134"" />
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <Import Project=""Sdk.targets"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.134"" />
  <Import Project=""Directory.Build.targets"" />
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TryUpdate_PreservesByteOrderMarkAndLineEndings(bool withBom)
        {
            var encoding = new UTF8Encoding(withBom);
            var original = encoding.GetPreamble().Concat(encoding.GetBytes("<Project Sdk=\"X.Sdk/1.0.0\">\r\n</Project>\r\n")).ToArray();
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { { ProjectPath, new MockFileData(original) } });

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));

            var expected = encoding.GetPreamble().Concat(encoding.GetBytes("<Project Sdk=\"X.Sdk/2.0.0\">\r\n</Project>\r\n")).ToArray();
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(ProjectPath));
        }

        [Fact]
        public void TryUpdate_Utf16File_PreservesEncoding()
        {
            var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            var original = encoding.GetPreamble().Concat(encoding.GetBytes("<Project Sdk=\"X.Sdk/1.0.0\">\r\n</Project>\r\n")).ToArray();
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { { ProjectPath, new MockFileData(original) } });

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));

            var expected = encoding.GetPreamble().Concat(encoding.GetBytes("<Project Sdk=\"X.Sdk/2.0.0\">\r\n</Project>\r\n")).ToArray();
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(ProjectPath));
        }

        [Fact]
        public void TryUpdate_NestedProjectElement_OnlyRootSdkAttributeIsUpdated()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""X.Sdk/1.0.0"">
  <ItemGroup>
    <Project Sdk=""X.Sdk/1.0.0"" />
  </ItemGroup>
</Project>");

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));

            Assert.Equal(@"<Project Sdk=""X.Sdk/2.0.0"">
  <ItemGroup>
    <Project Sdk=""X.Sdk/1.0.0"" />
  </ItemGroup>
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_GreaterThanInsideQuotedAttribute_DoesNotEndTag()
        {
            var fileSystem = CreateFileSystem(@"<Project>
  <Import Project=""Sdk.props"" Condition=""'1' > '0'"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.0"" />
  <Sdk Label=""a > b"" Name=""Aspire.AppHost.Sdk"" Version=""9.5.0"" />
  <Import Project=""Sdk.targets"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.0"" />
</Project>");

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Microsoft.Build.NoTargets", new NuGetVersion("3.7.134")));
            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Aspire.AppHost.Sdk", new NuGetVersion("9.5.2")));

            Assert.Equal(@"<Project>
  <Import Project=""Sdk.props"" Condition=""'1' > '0'"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.134"" />
  <Sdk Label=""a > b"" Name=""Aspire.AppHost.Sdk"" Version=""9.5.2"" />
  <Import Project=""Sdk.targets"" Sdk=""Microsoft.Build.NoTargets"" Version=""3.7.134"" />
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_VersionTextInsideAnotherAttribute_IsNotChanged()
        {
            var fileSystem = CreateFileSystem(@"<Project><Sdk Label=""Version='1.0.0'"" Name=""X.Sdk"" Version=""1.0.0"" /></Project>");

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(@"<Project><Sdk Label=""Version='1.0.0'"" Name=""X.Sdk"" Version=""2.0.0"" /></Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void Discover_And_TryUpdate_IgnoreProjectExtensions()
        {
            const string extensions = @"<ProjectExtensions>
    <VisualStudio>
      <Sdk Name=""Microsoft.Build.NoTargets"" Version=""1.0.0"" />
      <Import Project=""Sdk.props"" Sdk=""Microsoft.Build.NoTargets"" Version=""1.0.0"" />
    </VisualStudio>
  </ProjectExtensions>";
            var fileSystem = CreateFileSystem($@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.0"">
  {extensions}
</Project>");

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
            Assert.Equal(new NuGetVersion("3.7.0"), reference.ResolvedVersion);
            Assert.Single(ProjectSdkReferenceHelper.GetDeclarations(fileSystem, ProjectPath));

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Microsoft.Build.NoTargets", new NuGetVersion("3.7.134")));
            Assert.Equal($@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.134"">
  {extensions}
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_PropertyBackedVersion_IsNotChanged()
        {
            const string content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <Sdk Name=""Aspire.AppHost.Sdk"" Version=""$(AspireVersion)"" />
</Project>";
            var fileSystem = CreateFileSystem(content);

            var updated = ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Aspire.AppHost.Sdk", new NuGetVersion("13.0.2"));

            Assert.False(updated);
            Assert.Equal(content, fileSystem.File.ReadAllText(ProjectPath));
        }

        [Fact]
        public void TryUpdate_AlreadyAtVersion_ReturnsFalse()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.2""></Project>");

            var updated = ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "Aspire.AppHost.Sdk", new NuGetVersion("13.0.2"));

            Assert.False(updated);
            Assert.Equal("13.0.2", ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath).Single().ResolvedVersion.ToString());
        }

        // Entity references are decoded, as discovery decodes them, so an SDK it reports is updated.
        [Theory]
        [InlineData(@"<Project Sdk=""X&#46;Sdk/1.0.0""></Project>", @"<Project Sdk=""X&#46;Sdk/2.0.0""></Project>")]
        [InlineData(@"<Project Sdk=""X.Sdk/1&#46;0&#46;0""></Project>", @"<Project Sdk=""X.Sdk/2.0.0""></Project>")]
        [InlineData(@"<Project><Sdk Name=""X&#46;Sdk"" Version=""1.0.0"" /></Project>", @"<Project><Sdk Name=""X&#46;Sdk"" Version=""2.0.0"" /></Project>")]
        [InlineData(@"<Project><Sdk Name=""X.Sdk"" Version=""1&#46;0&#46;0"" /></Project>", @"<Project><Sdk Name=""X.Sdk"" Version=""2.0.0"" /></Project>")]
        [InlineData(@"<Project><Import Project=""Sdk.props"" Sdk=""X&#46;Sdk"" Version=""1.0.0"" /></Project>", @"<Project><Import Project=""Sdk.props"" Sdk=""X&#46;Sdk"" Version=""2.0.0"" /></Project>")]
        public void Discover_And_TryUpdate_EntityReference_IsDiscoveredAndUpdated(string content, string expected)
        {
            var fileSystem = CreateFileSystem(content);

            var reference = Assert.Single(ProjectSdkReferenceHelper.Discover(fileSystem, ProjectPath));
            Assert.Equal("X.Sdk", reference.Name);
            Assert.Equal(new NuGetVersion("1.0.0"), reference.ResolvedVersion);

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(expected, fileSystem.File.ReadAllText(ProjectPath));
        }

        // Only the version of the matching entry in the root's Sdk attribute is replaced; the other entries keep their text.
        [Fact]
        public void TryUpdate_ProjectSdkAttributeWithEscapedEntry_KeepsItEscaped()
        {
            var fileSystem = CreateFileSystem(@"<Project Sdk=""A&amp;B.Sdk;X.Sdk/1.0.0""></Project>");

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(@"<Project Sdk=""A&amp;B.Sdk;X.Sdk/2.0.0""></Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        // Only an <Sdk> element directly under <Project> declares an SDK, so an Sdk item in an ItemGroup is neither
        // discovered nor updated.
        [Fact]
        public void Discover_And_TryUpdate_SdkItemInItemGroup_IsNotDiscoveredOrUpdated()
        {
            const string content = @"<Project Sdk=""X.Sdk/1.0.0"">
  <ItemGroup>
    <Sdk Include=""a"" Name=""X.Sdk"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
            var fileSystem = CreateFileSystem(content);

            Assert.Single(ProjectSdkReferenceHelper.GetDeclarations(fileSystem, ProjectPath));

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(@"<Project Sdk=""X.Sdk/2.0.0"">
  <ItemGroup>
    <Sdk Include=""a"" Name=""X.Sdk"" Version=""1.0.0"" />
  </ItemGroup>
</Project>", fileSystem.File.ReadAllText(ProjectPath));
        }

        // Only the matching entry's version is replaced, so line breaks, entity references and spacing elsewhere in
        // the root's Sdk attribute are kept.
        [Theory]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk;\n    X.Sdk/1.0.0\">\n</Project>", "<Project Sdk=\"Microsoft.NET.Sdk;\n    X.Sdk/2.0.0\">\n</Project>")]
        [InlineData("<Project Sdk=\"X.Sdk/1.0.0 ;\r\n\tOther.Sdk/1.0.0\"></Project>", "<Project Sdk=\"X.Sdk/2.0.0 ;\r\n\tOther.Sdk/1.0.0\"></Project>")]
        [InlineData("<Project Sdk=\"A&#59;B;X.Sdk/1.0.0\"></Project>", "<Project Sdk=\"A&#59;B;X.Sdk/2.0.0\"></Project>")]
        [InlineData("<Project Sdk='X.Sdk/1.0.0;Y&apos;.Sdk'></Project>", "<Project Sdk='X.Sdk/2.0.0;Y&apos;.Sdk'></Project>")]
        public void TryUpdate_ProjectSdkAttribute_KeepsTheRestOfTheAttributeText(string content, string expected)
        {
            var fileSystem = CreateFileSystem(content);

            Assert.True(ProjectSdkReferenceHelper.TryUpdate(fileSystem, ProjectPath, "X.Sdk", new NuGetVersion("2.0.0")));
            Assert.Equal(expected, fileSystem.File.ReadAllText(ProjectPath));
        }
    }
}
