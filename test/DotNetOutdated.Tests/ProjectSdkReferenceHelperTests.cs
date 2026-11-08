using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
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
    }
}
