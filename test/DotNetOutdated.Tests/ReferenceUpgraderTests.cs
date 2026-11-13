using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class ReferenceUpgraderTests
    {
        private const string DirectoryPackagesPropsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""12.0.3"" />
    <PackageVersion Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>";

        private const string SdkProjectContent = @"<Project Sdk=""Microsoft.NET.Sdk""></Project>";

        private const string GlobalPackageReferenceContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <GlobalPackageReference Include=""SomeAnalyzer"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";

        private const string DirectoryBuildPropsContent = @"<Project>
  <ItemGroup>
    <PackageReference
        Include=""Contoso.Widgets""
        Version=""1.0.0""
        PrivateAssets=""all""
        Condition=""$(MSBuildProjectExtension) == '.csproj'"" />
    <PackageReference Include=""Contoso.Gadgets"" Version=""2.0.0"" />
  </ItemGroup>
</Project>";

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesDirectoryPackagesProps_DoesNotCallDotNet()
        {
            // Arrange
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act
            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: true);

            // Assert
            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);

            var updatedContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("Version=\"13.0.1\"", updatedContent);
            Assert.DoesNotContain("Version=\"12.0.3\"", updatedContent);
            // Serilog should be untouched
            Assert.Contains("Version=\"2.10.0\"", updatedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesMultilinePackageVersion()
        {
            const string propsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion
      Include=""Microsoft.AspNetCore.Components.WebAssembly.Authentication""
      Version=""9.0.7""
    />
  </ItemGroup>
</Project>";
            const string expectedContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion
      Include=""Microsoft.AspNetCore.Components.WebAssembly.Authentication""
      Version=""10.0.0""
    />
  </ItemGroup>
</Project>";

            AssertNoRestoreUpdate(propsContent, "Microsoft.AspNetCore.Components.WebAssembly.Authentication", "10.0.0", expectedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesPackageVersionWithChildElements()
        {
            const string propsContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""SomeGenerator"" Version=""1.0.0"">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageVersion>
    <PackageVersion Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>";
            const string expectedContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""SomeGenerator"" Version=""2.0.0"">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageVersion>
    <PackageVersion Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>";

            AssertNoRestoreUpdate(propsContent, "SomeGenerator", "2.0.0", expectedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesGlobalPackageReferenceWithChildElements()
        {
            const string propsContent = @"<Project>
  <ItemGroup>
    <GlobalPackageReference Include=""coverlet.collector"" Version=""6.0.0"">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </GlobalPackageReference>
  </ItemGroup>
</Project>";
            const string expectedContent = @"<Project>
  <ItemGroup>
    <GlobalPackageReference Include=""coverlet.collector"" Version=""6.0.2"">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </GlobalPackageReference>
  </ItemGroup>
</Project>";

            AssertNoRestoreUpdate(propsContent, "coverlet.collector", "6.0.2", expectedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_UnmatchedEntry_ReportsFailureAndLeavesFileUnchanged()
        {
            // The package is mentioned, but no entry has a version to update.
            const string propsContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""SomePackage"">
      <Version><!-- pinned -->1.0.0</Version>
    </PackageVersion>
  </ItemGroup>
</Project>";
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "SomePackage", "net8.0", new NuGetVersion("2.0.0"), noRestore: true);

            Assert.False(result.IsSuccess);
            Assert.Contains("SomePackage", result.FailureDetails);
            Assert.Equal(
                $"Could not update 'SomePackage' in '{propsPath}': no <PackageVersion> or <GlobalPackageReference> element was found " +
                "with an Include or Update attribute and a version (a Version attribute or <Version> child element). Update the version manually.",
                result.FailureDetails);
            // Must not fall back to `dotnet add package --no-restore`, which writes the version into the project file.
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(propsContent, mockFileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void CpmProjectWithNoRestore_EntryAlreadyAtVersion_ReportsSuccess()
        {
            // A multi-targeted project upgrades the same central entry once per framework;
            // the second call finds the version already written and must still succeed.
            AssertNoRestoreUpdate(DirectoryPackagesPropsContent, "Newtonsoft.Json", "12.0.3", DirectoryPackagesPropsContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_PackageIdCasingDiffers_UpdatesEntryAndKeepsItsCasing()
        {
            // NuGet package ids are case-insensitive.
            const string propsContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""newtonsoft.json"" Version=""12.0.3"" />
  </ItemGroup>
</Project>";
            const string expectedContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""newtonsoft.json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";

            AssertNoRestoreUpdate(propsContent, "Newtonsoft.Json", "13.0.1", expectedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_PackageIdCasingDiffers_MatchesIndependentOfCulture()
        {
            // Under tr-TR, culture-aware case folding does not map 'I' to 'i'.
            const string propsContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""microsoft.identitymodel.tokens"" Version=""7.0.0"" />
  </ItemGroup>
</Project>";
            const string expectedContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""microsoft.identitymodel.tokens"" Version=""8.0.0"" />
  </ItemGroup>
</Project>";

            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                AssertNoRestoreUpdate(propsContent, "Microsoft.IdentityModel.Tokens", "8.0.0", expectedContent);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Theory]
        [InlineData("<PackageVersion Version=\"{0}\" Include=\"Newtonsoft.Json\" />")]
        [InlineData("<PackageVersion Include=\"Newtonsoft.Json\" Condition=\"'$(CI)' == 'true'\" Version=\"{0}\" />")]
        [InlineData("<PackageVersion Include='Newtonsoft.Json' Version='{0}' />")]
        [InlineData("<PackageVersion Include = \"Newtonsoft.Json\" Version = \"{0}\" />")]
        [InlineData("<GlobalPackageReference Version=\"{0}\" Include=\"Newtonsoft.Json\" PrivateAssets=\"all\" />")]
        public void CpmProjectWithNoRestore_UpdatesAnyAttributeLayout(string packageElement)
        {
            var propsContent = "<Project>\n  <ItemGroup>\n    " + string.Format(packageElement, "12.0.3") + "\n  </ItemGroup>\n</Project>\n";
            var expectedContent = "<Project>\n  <ItemGroup>\n    " + string.Format(packageElement, "13.0.1") + "\n  </ItemGroup>\n</Project>\n";

            AssertNoRestoreUpdate(propsContent, "Newtonsoft.Json", "13.0.1", expectedContent);
        }

        [Theory]
        [InlineData(
            "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" Notes=\"Version='legacy'\" />",
            "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" Notes=\"Version='legacy'\" />")]
        [InlineData(
            "<PackageVersion Include=\"Newtonsoft.Json\" Condition=\"'$(LangVersion)' > '10'\" Version=\"12.0.3\" />",
            "<PackageVersion Include=\"Newtonsoft.Json\" Condition=\"'$(LangVersion)' > '10'\" Version=\"13.0.1\" />")]
        [InlineData(
            "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"$([System.String]::Copy('12.0.3'))\" />",
            "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />")]
        [InlineData(
            "<PackageVersion Label=\"Include='Newtonsoft.Json'\" Include=\"Other.Package\" Version=\"12.0.3\" />\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />",
            "<PackageVersion Label=\"Include='Newtonsoft.Json'\" Include=\"Other.Package\" Version=\"12.0.3\" />\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />")]
        public void CpmProjectWithNoRestore_UpdatesOnlyTheVersionAttributeValue(string packageElement, string expectedElement)
        {
            var propsContent = "<Project>\n  <ItemGroup>\n    " + packageElement + "\n  </ItemGroup>\n</Project>\n";
            var expectedContent = "<Project>\n  <ItemGroup>\n    " + expectedElement + "\n  </ItemGroup>\n</Project>\n";

            AssertNoRestoreUpdate(propsContent, "Newtonsoft.Json", "13.0.1", expectedContent);
        }

        [Fact]
        public void CpmProjectWithNoRestore_DoesNotRewriteCommentedOutEntry()
        {
            const string propsContent = "<Project>\n  <!-- <PackageVersion Include=\"Newtonsoft.Json\" Version=\"11.0.1\" /> old pin -->\n" +
                "  <ItemGroup>\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />\n  </ItemGroup>\n</Project>\n";

            AssertNoRestoreUpdate(propsContent, "Newtonsoft.Json", "13.0.1", propsContent.Replace("12.0.3", "13.0.1"));
        }

        private static void AssertNoRestoreUpdate(string propsContent, string packageName, string version, string expectedContent)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, packageName, "net8.0", new NuGetVersion(version), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(expectedContent, mockFileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void CpmProjectWithoutNoRestore_CallsDotNetAddPackage()
        {
            // Arrange
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act
            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            // Assert
            dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);

            // Directory.Packages.props should NOT be modified (dotnet handles it when restore runs)
            var content = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("Version=\"12.0.3\"", content);
        }

        [Fact]
        public void NonCpmProjectWithNoRestore_FallsThroughToDotNetAddPackage()
        {
            // Arrange - no Directory.Packages.props exists
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act
            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: true);

            // Assert - should fall through to dotnet add package
            dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);
        }

        [Theory]
        [InlineData("\n", "", false)]
        [InlineData("\n", "\n", false)]
        [InlineData("\n", "\n\n", false)]
        [InlineData("\r\n", "", false)]
        [InlineData("\r\n", "\r\n", false)]
        [InlineData("\r\n", "\r\n\r\n", false)]
        [InlineData("\n", "\n\n", true)]
        [InlineData("\r\n", "\r\n\r\n", true)]
        public void CpmProject_PreservesPropsWhitespace(string newline, string trailingWhitespace, bool noRestore)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var original = string.Join(newline,
                "<Project>",
                "",
                "\t<PropertyGroup>",
                "\t\t<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
                "\t</PropertyGroup>",
                "  ",
                "\t<!-- Keep packages separate from properties. -->",
                "\t<ItemGroup>",
                "\t\t<PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />",
                "",
                "\t\t<PackageVersion Include=\"Serilog\" Version=\"2.10.0\" />",
                "\t</ItemGroup>",
                "",
                "</Project>") + trailingWhitespace;
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                // dotnet add package reformats the props file while upgrading the version.
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1")
                    .Replace("  <ItemGroup>", "  <!-- Keep packages separate from properties. -->\n  <ItemGroup>"));
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
            if (!noRestore)
            {
                dotNetRunner.Received(1).Run(fileSystem.Path.GetDirectoryName(projectPath),
                    Arg.Is<string[]>(args => string.Join(" ", args) == "add MyProject.csproj package Newtonsoft.Json -v 13.0.1 -f net8.0"));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CpmProject_WithVersionRange_PreservesPropsWhitespace(bool noRestore)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var original = string.Join("\r\n",
                "<Project>",
                "",
                "\t<PropertyGroup>",
                "\t\t<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
                "\t</PropertyGroup>",
                "\t<ItemGroup>",
                "\t\t<PackageVersion Include=\"Newtonsoft.Json\" Version=\"[12.0.1,14.0.0)\" />",
                "",
                "\t\t<PackageVersion Include=\"Serilog\" Version=\"2.10.0\" />",
                "\t</ItemGroup>",
                "</Project>") + "\r\n\r\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                // dotnet add package reformats the props file while writing the range it was given.
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "[13.0.4, 14.0.0)"));
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.4"), noRestore, ignoreFailedSources: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("[12.0.1,14.0.0)", "[13.0.4, 14.0.0)"), fileSystem.File.ReadAllText(propsPath));
            if (!noRestore)
            {
                dotNetRunner.Received(1).Run(fileSystem.Path.GetDirectoryName(projectPath),
                    Arg.Is<string[]>(args => string.Join(" ", args) == "add MyProject.csproj package Newtonsoft.Json -v [13.0.4, 14.0.0) -f net8.0"));
            }
        }

        [Theory]
        [InlineData(false, "[12.0.1,14.0.0)", true)]
        [InlineData(true, "[13.0.4,14.0.0)", true)]
        [InlineData(true, "[12.0.1,14.0.0)", false)]
        public void CpmVariableWithNoRestore_ReportsFailureWhenRangeCannotBeWritten(bool readOnly, string currentValue, bool expectSuccess)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData($@"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NewtonsoftJsonVersion>{currentValue}</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                },
                { projectPath, new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""Newtonsoft.Json"" /></ItemGroup></Project>") }
            });
            if (readOnly)
            {
                fileSystem.File.SetAttributes(propsPath, FileAttributes.ReadOnly);
            }

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.4"), noRestore: true, ignoreFailedSources: false);

            Assert.Equal(expectSuccess, result.IsSuccess);
            if (!expectSuccess)
            {
                Assert.Contains("$(NewtonsoftJsonVersion)", result.FailureDetails);
            }
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void CpmProjectWithRestore_PreservesWhitespaceAcrossMultipleUpgrades()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = DirectoryPackagesPropsContent.Replace("  <PropertyGroup>", "\n  <PropertyGroup>") + "\n\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(call =>
            {
                var arguments = call.ArgAt<string[]>(1);
                var oldVersion = arguments[3] == "Newtonsoft.Json" ? "12.0.3" : "2.10.0";
                var content = fileSystem.File.ReadAllText(propsPath).Replace(oldVersion, arguments[5]);
                fileSystem.File.WriteAllText(propsPath, System.Xml.Linq.XDocument.Parse(content).ToString());
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var first = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore: false);
            var second = service.Upgrade(projectPath, "Serilog", "net8.0", new NuGetVersion("2.11.0"), noRestore: false);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1").Replace("2.10.0", "2.11.0"), fileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void CpmProjectWithRestore_PreservesAdditionalChangesFromDotNet()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var updated = DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1")
                .Replace("</ItemGroup>", "  <PackageVersion Include=\"Other.Package\" Version=\"1.0.0\" />\n  </ItemGroup>");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, updated);
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(updated, fileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData("<PackageVersion Version='12.0.3' Include='Newtonsoft.Json' />")]
        [InlineData("<PackageVersion\n      Include = \"Newtonsoft.Json\"\n      Version = \"12.0.3\"\n    />")]
        [InlineData("<PackageVersion Include=\"newtonsoft.json\" Version=\"12.0.3\"></PackageVersion>")]
        public void CpmProjectWithRestore_PreservesPackageAttributeFormatting(string packageElement)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project>\n\n  <ItemGroup>\n    " + packageElement + "\n  </ItemGroup>\n\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath,
                    System.Xml.Linq.XDocument.Parse(original.Replace("12.0.3", "13.0.1")).ToString());
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData("<!--\n<PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />\n-->")]
        [InlineData("<PropertyGroup><Notes><![CDATA[<PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />]]></Notes></PropertyGroup>")]
        [InlineData("<?example <PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" /> ?>")]
        public void CpmProjectWithRestore_PreservesInactivePackageMarkup(string inactiveMarkup)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            const string itemGroup = "  <ItemGroup>\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />\n  </ItemGroup>";
            var original = "<Project>\n\n" + inactiveMarkup + "\n\n" + itemGroup + "\n\n</Project>\n\n";
            var expected = original.Replace(itemGroup, itemGroup.Replace("12.0.3", "13.0.1"));
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, System.Xml.Linq.XDocument.Parse(expected).ToString());
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(expected, fileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CpmProjectWithRestore_PreservesPropsEncoding(bool utf16, bool trailingWhitespace)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = DirectoryPackagesPropsContent + (trailingWhitespace ? "\r\n\r\n" : "");
            var encoding = utf16 ? Encoding.Unicode : Encoding.UTF8;
            byte[] originalBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(originalBytes) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1"));
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(original.Replace("12.0.3", "13.0.1"))];
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(propsPath));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, true)] // UTF-16 is only detectable through its byte order mark.
        public void CpmProjectWithNoRestore_PreservesPropsEncoding(bool utf16, bool bom)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = DirectoryPackagesPropsContent.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            Encoding encoding = utf16 ? new UnicodeEncoding(false, bom) : new UTF8Encoding(bom);
            byte[] originalBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(originalBytes) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: true);

            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(original.Replace("12.0.3", "13.0.1"))];
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(propsPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CpmProjectWithRestore_DoesNotRewriteUnsupportedEncoding(bool cliUpdatesFile)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n<!-- caf\u00e9 -->\n" + DirectoryPackagesPropsContent;
            var originalBytes = Encoding.Latin1.GetBytes(original);
            var updatedBytes = Encoding.Latin1.GetBytes(original.Replace("12.0.3", "13.0.1"));
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(originalBytes) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                if (cliUpdatesFile)
                {
                    fileSystem.File.WriteAllBytes(propsPath, updatedBytes);
                }
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(cliUpdatesFile ? updatedBytes : originalBytes, fileSystem.File.ReadAllBytes(propsPath));
        }

        [Theory]
        [InlineData("Directory.Packages.props")]
        [InlineData("Directory.Build.props")]
        public void CpmProjectWithRestore_PreservesWhitespaceAndVariableReferences(string propertyFileName)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var propertyPath = XFS.Path($@"c:\repo\{propertyFileName}");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            const string propertyContent = "<Project>\n\n  <PropertyGroup>\n    <JsonVersion>12.0.3</JsonVersion>\n  </PropertyGroup>\n\n</Project>\n\n";
            var original = DirectoryPackagesPropsContent.Replace("12.0.3", "$(JsonVersion)") + "\n\n";
            if (propertyPath == propsPath)
            {
                original = original.Replace("  <PropertyGroup>", "\n  <PropertyGroup>\n    <JsonVersion>12.0.3</JsonVersion>");
            }
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            if (propertyPath != propsPath)
            {
                fileSystem.AddFile(propertyPath, new MockFileData(propertyContent));
            }
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                var rewritten = System.Xml.Linq.XDocument.Parse(original.Replace("$(JsonVersion)", "13.0.1")).ToString();
                fileSystem.File.WriteAllText(propsPath, rewritten);
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
            if (propertyPath != propsPath)
            {
                Assert.Equal(propertyContent.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propertyPath));
            }
        }

        [Theory]
        [InlineData("<PackageVersion Include='Newtonsoft.Json' Version='$(JsonVersion)' />")]
        [InlineData("<PackageVersion Include = \"Newtonsoft.Json\" Version = \"$(JsonVersion)\" />")]
        [InlineData("<PackageVersion Version=\"$(JsonVersion)\" Include=\"Newtonsoft.Json\" />")]
        [InlineData("<PackageVersion\n      Include=\"Newtonsoft.Json\"\n      Version=\"$(JsonVersion)\" />")]
        public void CpmProjectWithRestore_RestoresVariableReferenceInOriginalAttributeFormatting(string packageElement)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project>\n\n  <PropertyGroup>\n    <JsonVersion>12.0.3</JsonVersion>\n  </PropertyGroup>\n\n" +
                "  <ItemGroup>\n    " + packageElement + "\n  </ItemGroup>\n\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                // dotnet add package writes a literal version and normalizes the layout.
                fileSystem.File.WriteAllText(propsPath,
                    System.Xml.Linq.XDocument.Parse(original.Replace("$(JsonVersion)", "13.0.1")).ToString());
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CpmProject_VariableReferenceUpdateKeepsPropsEncoding(bool utf16, bool noRestore)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var encoding = utf16 ? Encoding.Unicode : Encoding.UTF8;
            var declaration = $"<?xml version=\"1.0\" encoding=\"{(utf16 ? "utf-16" : "utf-8")}\"?>\r\n";
            var original = declaration + "<Project>\r\n\r\n  <PropertyGroup>\r\n    <JsonVersion>12.0.3</JsonVersion>\r\n  </PropertyGroup>\r\n\r\n" +
                "  <ItemGroup>\r\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"$(JsonVersion)\" />\r\n  </ItemGroup>\r\n</Project>\r\n";
            byte[] originalBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(originalBytes) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, original.Replace("$(JsonVersion)", "13.0.1"));
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore);

            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(original.Replace("12.0.3", "13.0.1"))];
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(propsPath));
        }

        [Fact]
        public void CpmProjectWithRestore_FailedUpgradeDoesNotRestoreProps()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent + "\n\n") },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var updated = DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, updated);
                return new RunStatus("", "Package is incompatible", 1);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.False(result.IsSuccess);
            Assert.Equal(updated, fileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void CpmProjectWithNoRestore_WalksUpDirectoryTree()
        {
            // Arrange - props file is two levels up from the project
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\deep\nested\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act
            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: true);

            // Assert
            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);

            var updatedContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("Version=\"13.0.1\"", updatedContent);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CpmProject_PreservesImportedParentPropsWhitespace(bool noRestore)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var nestedPropsPath = XFS.Path(@"c:\repo\src\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var original = DirectoryPackagesPropsContent.Replace("  <PropertyGroup>", "\n  <PropertyGroup>") + "\n\n";
            const string nestedContent = "<Project>\n  <Import Project=\"../Directory.Packages.props\" />\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { nestedPropsPath, new MockFileData(nestedContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1"));
                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
            Assert.Equal(nestedContent, fileSystem.File.ReadAllText(nestedPropsPath));
            if (noRestore)
            {
                dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            }
        }

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesGlobalPackageReference()
        {
            // Arrange
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(GlobalPackageReferenceContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act
            var result = service.Upgrade(projectPath, "SomeAnalyzer", "net8.0",
                new NuGetVersion("2.0.0"), noRestore: true);

            // Assert
            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);

            var updatedContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("Version=\"2.0.0\"", updatedContent);
            Assert.DoesNotContain("Version=\"1.0.0\"", updatedContent);
        }

        // `dotnet add package` can't update a GlobalPackageReference: it adds a PackageReference, which central package
        // management rejects (#681). The version is written directly and the project restored.
        [Theory]
        [InlineData("<GlobalPackageReference Include=\"StyleCop.Analyzers\" Version=\"{0}\" PrivateAssets=\"All\" />")]
        [InlineData("<GlobalPackageReference Version='{0}' Update='StyleCop.Analyzers' PrivateAssets='all' />")]
        [InlineData("<GlobalPackageReference Include=\"StyleCop.Analyzers\" Version=\"{0}\">\n      <PrivateAssets>all</PrivateAssets>\n    </GlobalPackageReference>")]
        public void CpmProjectWithRestore_UpdatesGlobalPackageReferenceAndRestores(string packageElement)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string propsTemplate = "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n" +
                "  <ItemGroup Condition=\"'$(MSBuildProjectExtension)' == '.csproj'\">\n    {0}\n  </ItemGroup>\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(string.Format(propsTemplate, string.Format(packageElement, "1.2.0-beta.556"))) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "StyleCop.Analyzers", "net8.0",
                new NuGetVersion("1.3.180"), noRestore: false);

            Assert.True(result.IsSuccess, result.FailureDetails);
            Assert.Equal(
                string.Format(propsTemplate, string.Format(packageElement, "1.3.180")),
                fileSystem.File.ReadAllText(propsPath));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\src\MyProject"), Arg.Is<string[]>(a => a.Length == 2 && a[0] == "restore" && a[1] == "MyProject.csproj"));
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
        }

        [Fact]
        public void CpmProjectWithRestore_GlobalPackageReferenceSharedByTargetFrameworks_RestoresOnce()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(GlobalPackageReferenceContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);
            var version = new NuGetVersion("2.0.0");

            Assert.True(service.Upgrade(projectPath, "SomeAnalyzer", "net8.0", version, noRestore: false).IsSuccess);
            Assert.True(service.Upgrade(projectPath, "SomeAnalyzer", "net9.0", version, noRestore: false).IsSuccess);

            Assert.Equal(GlobalPackageReferenceContent.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(propsPath));
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void GlobalPackageReferenceInDirectoryBuildProps_IsUpdatedWhereItIsDeclared(bool noRestore)
        {
            var buildPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            const string buildProps = "<Project>\n  <ItemGroup>\n    <GlobalPackageReference Include=\"SomeAnalyzer\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { buildPropsPath, new MockFileData(buildProps) },
                { XFS.Path(@"c:\repo\Directory.Packages.props"), new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "SomeAnalyzer", "net8.0", new NuGetVersion("2.0.0"), noRestore);

            Assert.True(result.IsSuccess, result.FailureDetails);
            Assert.Equal(buildProps.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(buildPropsPath));
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
        }

        [Fact]
        public void CpmProjectWithNoRestore_PackageNotInProps_FallsThrough()
        {
            // Arrange - Directory.Packages.props exists but doesn't contain the package
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            // Act - package "UnknownPackage" is not in Directory.Packages.props
            var result = service.Upgrade(projectPath, "UnknownPackage", "net8.0",
                new NuGetVersion("1.0.0"), noRestore: true);

            // Assert - should fall through to dotnet add package
            dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);
        }

        [Fact]
        public void FileBasedAppVariablePackageWithNoRestore_UpdatesVariableWithoutCallingDotNet()
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#!/usr/bin/env dotnet
#:property HumanizerPackageId=Humanizer
#:property HumanizerPackageVersion=3.0.10
#:package $(HumanizerPackageId)@$(HumanizerPackageVersion)
Console.WriteLine();")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property HumanizerPackageVersion=3.0.11", content);
            Assert.Contains("#:package $(HumanizerPackageId)@$(HumanizerPackageVersion)", content);
        }

        [Fact]
        public void FileBasedAppVariablePackageWithRestore_UpdatesVariableAndRunsRestore()
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#!/usr/bin/env dotnet
#:property HumanizerPackageVersion=3.0.10
#:package Humanizer@$(HumanizerPackageVersion)
Console.WriteLine();")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: false);

            Assert.True(result.IsSuccess);
            dotNetRunner.Received().Run(XFS.Path(@"c:\repo"), Arg.Is<string[]>(a => a[0] == "restore" && a[1] == "app.cs"));
        }

        [Fact]
        public void FileBasedAppDirectPackage_FallsThroughToDotNetAddPackage()
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#!/usr/bin/env dotnet
#:package Humanizer@3.0.10
Console.WriteLine();")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.Received().Run(
                XFS.Path(@"c:\repo"),
                Arg.Is<string[]>(a =>
                    a[0] == "add" &&
                    a[1] == "app.cs" &&
                    a[2] == "package" &&
                    a[3] == "Humanizer" &&
                    a[4] == "-v" &&
                    a[5] == "3.0.11" &&
                    a[6] == "--no-restore"));
        }

        [Fact]
        public void FileBasedAppDirectSdk_UpdatesDirectiveWithoutFrameworkFlag()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk Cake.Sdk@6.0.0
Information(""Outdated Sdk"");")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0", content);
            Assert.DoesNotContain("#:sdk Cake.Sdk@6.0.0", content);
        }

        [Fact]
        public void FileBasedAppSdkWithUnresolvedPropertyReference_ReturnsFailureWithoutCallingDotNet()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            const string content = "#:sdk $(SdkId)@6.0.0";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData(content) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.False(result.IsSuccess);
            Assert.Contains("Failed to update file-based app", result.FailureDetails);
            Assert.Equal(content, mockFileSystem.File.ReadAllText(appPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void FileBasedAppVariableSdkIdAndVersion_UpdatesViaVariablePath()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:property SdkId=Cake.Sdk
#:property SdkVersion=6.0.0
#:sdk $(SdkId)@$(SdkVersion)
Information(""Outdated Sdk"");")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property SdkVersion=6.2.0", content);
            Assert.Contains("#:sdk $(SdkId)@$(SdkVersion)", content);
        }

        [Fact]
        public void FileBasedAppVariableUpdate_NoOpWhenAlreadyAtVersion_ReturnsSuccess()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:property SdkId=Cake.Sdk
#:property SdkVersion=6.2.0
#:sdk $(SdkId)@$(SdkVersion)
Information(""Outdated Sdk"");")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property SdkVersion=6.2.0", content);
        }

        [Fact]
        public void FileBasedAppDirectSdk_NoOpWhenAlreadyAtVersion_ReturnsSuccess()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk Cake.Sdk@6.2.0
Information(""Outdated Sdk"");")
                }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0", mockFileSystem.File.ReadAllText(appPath));
        }

        [Fact]
        public void ProjectSdk_UpdatesProjectFileAndRestoresWithoutAddingPackage()
        {
            var projectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.0""></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Aspire.AppHost.Sdk", "net10.0", new NuGetVersion("13.0.2"), kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            Assert.Equal(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.2""></Project>", mockFileSystem.File.ReadAllText(projectPath));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\AppHost"), Arg.Is<string[]>(a => a.Length == 2 && a[0] == "restore" && a[1] == "AppHost.csproj"));
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
        }

        // Callers that don't know the kind (MCP clients that omit it) rely on the name: see #827.
        [Fact]
        public void FileBasedAppDirectSdk_WithoutKind_UpdatesSdkDirective()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData("#:property TargetFramework=net10.0\n#:sdk Cake.Sdk@6.0.0\n") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: null);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal("#:property TargetFramework=net10.0\n#:sdk Cake.Sdk@6.2.0\n", mockFileSystem.File.ReadAllText(appPath));
        }

        [Fact]
        public void FileBasedAppVariableSdk_WithoutKind_UpdatesVariable()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData("#:property SdkVersion=6.0.0\n#:sdk Cake.Sdk@$(SdkVersion)\n") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: null);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal("#:property SdkVersion=6.2.0\n#:sdk Cake.Sdk@$(SdkVersion)\n", mockFileSystem.File.ReadAllText(appPath));
        }

        [Fact]
        public void FileBasedAppDirectPackage_WithoutKind_UpdatesPackageWithDotNetAdd()
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData("#:package Humanizer@2.14.1\n") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: true, kind: null);

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Humanizer"));
        }

        [Fact]
        public void FileBasedAppSdk_WithPackageKind_FailsWithoutAddingAPackageDirective()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            const string original = "#:sdk Cake.Sdk@6.0.0\n";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData(original) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: ReferenceKind.Package);

            Assert.False(result.IsSuccess);
            Assert.Contains("#:sdk", result.FailureDetails);
            Assert.Equal(original, mockFileSystem.File.ReadAllText(appPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(ReferenceKind.Sdk)]
        [InlineData(ReferenceKind.Package)]
        public void FileBasedAppSdkAndPackageWithTheSameId_UpdatesTheSdkUnlessThePackageIsRequested(ReferenceKind? kind)
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            const string original = "#:sdk Cake.Sdk@6.0.0\n#:package Cake.Sdk@6.0.0\n";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData(original) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true, kind: kind);

            Assert.True(result.IsSuccess);
            if (kind == ReferenceKind.Package)
            {
                Assert.Equal(original, mockFileSystem.File.ReadAllText(appPath));
                dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Cake.Sdk"));
            }
            else
            {
                Assert.Equal("#:sdk Cake.Sdk@6.2.0\n#:package Cake.Sdk@6.0.0\n", mockFileSystem.File.ReadAllText(appPath));
                dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            }
        }

        [Fact]
        public void ProjectSdk_WithoutKind_UpdatesProjectFileWithoutAddingPackage()
        {
            var projectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.0""></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Aspire.AppHost.Sdk", "net10.0", new NuGetVersion("13.0.2"), noRestore: true, kind: null);

            Assert.True(result.IsSuccess);
            Assert.Equal(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.2""></Project>", mockFileSystem.File.ReadAllText(projectPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void ProjectPackage_WithoutKind_UpdatesPackageWithDotNetAdd()
        {
            var projectPath = XFS.Path(@"c:\repo\App\App.csproj");
            const string original = @"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""Newtonsoft.Json"" Version=""12.0.1"" /></ItemGroup></Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(original) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), kind: null);

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Newtonsoft.Json"));
        }

        [Theory]
        [InlineData(ReferenceKind.Package)]
        [InlineData(ReferenceKind.Sdk)]
        public void ProjectSdkAndPackageWithTheSameId_UpdatesOnlyTheRequestedKind(ReferenceKind kind)
        {
            var projectPath = XFS.Path(@"c:\repo\Build\Build.csproj");
            const string original = @"<Project Sdk=""Cake.Sdk/6.0.0""><ItemGroup><PackageReference Include=""Cake.Sdk"" Version=""6.0.0"" /></ItemGroup></Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(original) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.1.0"), noRestore: true, kind: kind);

            Assert.True(result.IsSuccess);
            if (kind == ReferenceKind.Sdk)
            {
                Assert.Equal(original.Replace("Cake.Sdk/6.0.0", "Cake.Sdk/6.1.0"), mockFileSystem.File.ReadAllText(projectPath));
                dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            }
            else
            {
                Assert.Equal(original, mockFileSystem.File.ReadAllText(projectPath));
                dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Cake.Sdk"));
            }
        }

        [Theory]
        [InlineData(false, ReferenceKind.Package)]
        [InlineData(false, ReferenceKind.Sdk)]
        [InlineData(true, ReferenceKind.Package)]
        [InlineData(true, ReferenceKind.Sdk)]
        public void FileBasedAppSdkAndPackageWithTheSameId_UpdatesOnlyTheRequestedKindsProperty(bool sdkFirst, ReferenceKind kind)
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            const string packageLine = "#:package Cake.Sdk@$(PackageVersion)\n";
            const string sdkLine = "#:sdk Cake.Sdk@$(SdkVersion)\n";
            var original =
                "#:property PackageVersion=6.0.0\n" +
                "#:property SdkVersion=6.0.0\n" +
                (sdkFirst ? sdkLine + packageLine : packageLine + sdkLine) +
                "Console.WriteLine();\n";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData(original) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.1.0"), noRestore: true, kind: kind);

            Assert.True(result.IsSuccess, result.FailureDetails);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var updatedProperty = kind == ReferenceKind.Sdk ? "SdkVersion" : "PackageVersion";
            Assert.Equal(
                original.Replace($"#:property {updatedProperty}=6.0.0", $"#:property {updatedProperty}=6.1.0"),
                mockFileSystem.File.ReadAllText(appPath));
        }

        [Theory]
        [InlineData("#:sdk Cake.Sdk@6.0.0\n", ReferenceKind.Sdk)]
        [InlineData("#:property CakeVersion=6.0.0\n#:package Cake.Core@$(CakeVersion)\n", ReferenceKind.Package)]
        public void FileBasedApp_Update_PreservesByteOrderMark(string content, ReferenceKind kind)
        {
            var appPath = XFS.Path(@"c:\repo\build.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData([.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(content)]) }
            });
            var service = new ReferenceUpgrader(Substitute.For<IDotNetRunner>(), mockFileSystem);
            var name = kind == ReferenceKind.Sdk ? "Cake.Sdk" : "Cake.Core";

            var result = service.Upgrade(appPath, name, "net10.0", new NuGetVersion("6.1.0"), noRestore: true, kind: kind);

            Assert.True(result.IsSuccess, result.FailureDetails);
            var bytes = mockFileSystem.File.ReadAllBytes(appPath);
            Assert.Equal(System.Text.Encoding.UTF8.GetPreamble(), bytes[..3]);
            Assert.Equal(content.Replace("6.0.0", "6.1.0"), System.Text.Encoding.UTF8.GetString(bytes[3..]));
        }

        [Fact]
        public void ProjectSdk_ReadOnlyProjectFile_ReturnsFailureInsteadOfThrowing()
        {
            var projectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Aspire.AppHost.Sdk/13.0.0""></Project>") }
            });
            mockFileSystem.File.SetAttributes(projectPath, FileAttributes.ReadOnly);
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Aspire.AppHost.Sdk", "net10.0", new NuGetVersion("13.0.2"), kind: ReferenceKind.Sdk);

            Assert.False(result.IsSuccess);
            Assert.Contains("Aspire.AppHost.Sdk", result.FailureDetails);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void ProjectSdk_NoOpWhenAlreadyAtVersion_ReturnsSuccess()
        {
            var projectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><Sdk Name=""Aspire.AppHost.Sdk"" Version=""13.0.2"" /></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Aspire.AppHost.Sdk", "net10.0", new NuGetVersion("13.0.2"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void ProjectSdk_MultiTargetedProject_RestoresOnlyOnce()
        {
            var projectPath = XFS.Path(@"c:\repo\App\App.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.0""></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var first = service.Upgrade(projectPath, "Microsoft.Build.NoTargets", "net8.0", new NuGetVersion("3.7.134"), kind: ReferenceKind.Sdk);
            var second = service.Upgrade(projectPath, "Microsoft.Build.NoTargets", "netstandard2.0", new NuGetVersion("3.7.134"), kind: ReferenceKind.Sdk);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.Equal(@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.134""></Project>", mockFileSystem.File.ReadAllText(projectPath));
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "restore"));
        }

        [Theory]
        // Upper bound is kept while the new version is inside the declared range
        [InlineData("[6.0.13,7)", "6.0.36", "[6.0.36, 7.0.0)")]
        [InlineData("[12.0.1,14.0.0)", "13.0.4", "[13.0.4, 14.0.0)")]
        [InlineData("(6.0.0,8)", "7.0.0", "[7.0.0, 8.0.0)")]
        [InlineData("(,8.0.0)", "7.0.0", "[7.0.0, 8.0.0)")]
        [InlineData("[6.0.13,7]", "7.0.0", "[7.0.0]")]
        [InlineData("[6.*, 8)", "7.0.20", "[7.0.20, 8.0.0)")]
        // No upper bound: plain version
        [InlineData("6.0.13", "8.0.24", "8.0.24")]
        [InlineData("[6.0.13,)", "8.0.24", "8.0.24")]
        [InlineData("6.*", "8.0.24", "8.0.24")]
        [InlineData("4.0.0", "4.5.0.0", "4.5.0.0")]
        // New version outside the declared range (e.g. --maximum-version): drop the bound
        [InlineData("[6.0.13,7)", "8.0.24", "8.0.24")]
        [InlineData("[6.0.13,7]", "7.0.1", "7.0.1")]
        public void AddPackage_KeepsTheDeclaredUpperBoundWhileTheVersionSatisfiesIt(string declared, string version, string expectedArgument)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData($@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""Foo"" Version=""{declared}"" /></ItemGroup></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Foo", "net8.0", NuGetVersion.Parse(version));

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(
                Arg.Any<string>(),
                Arg.Is<string[]>(a => a[0] == "add" && a[4] == "-v" && a[5] == expectedArgument));
        }

        [Fact]
        public void AddPackage_OfATransitivePackage_WritesAPlainVersion()
        {
            // e.g. AWSSDK.S3 depends on AWSSDK.Core [3.7.0, 3.8.0); promoting AWSSDK.Core must not copy that bound,
            // and nothing in the project declares one.
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""AWSSDK.S3"" Version=""3.7.0"" /></ItemGroup></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "AWSSDK.Core", "net8.0", NuGetVersion.Parse("3.7.501.1"));

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[5] == "3.7.501.1"));
        }

        [Fact]
        public void AddPackage_WithNuGetVersion_WritesVersionExactlyAsBefore()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            service.Upgrade(projectPath, "Some.Package", "net8.0", new NuGetVersion("4.5.0.0"));

            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[5] == "4.5.0.0"));
        }

        [Fact]
        public void CpmProjectWithNoRestore_WritesVersionRangeToDirectoryPackagesProps()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent.Replace("12.0.3", "[12.0.3, 14.0.0)")) },
                { projectPath, new MockFileData(SdkProjectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.4"), noRestore: true, ignoreFailedSources: false);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Contains("Include=\"Newtonsoft.Json\" Version=\"[13.0.4, 14.0.0)\"", mockFileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData("MyProject.csproj", "PackageReference", "Include", "Version", true)]
        [InlineData("MyProject.csproj", "PackageReference", "Include", "VersionOverride", false)]
        [InlineData("Directory.Build.props", "PackageReference", "Include", "Version", false)]
        [InlineData("Directory.Build.props", "PackageReference", "Update", "Version", false)]
        [InlineData("Directory.Packages.props", "GlobalPackageReference", "Include", "Version", false)]
        public void DirectVariableUpdatePaths_UpdatesPropertyAndRestoresOnceWhenNeeded(
            string targetFile, string elementType, string idAttr, string versionAttr, bool noRestore)
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var targetPath = targetFile == "MyProject.csproj" ? projectPath : XFS.Path($@"c:\repo\{targetFile}");
            var targetContent = $"<Project><PropertyGroup><MauiVersion>8.0.3</MauiVersion></PropertyGroup>" +
                                $"<ItemGroup><{elementType} {idAttr}=\"Microsoft.Maui.Controls\" {versionAttr}=\"$(MauiVersion)\" /></ItemGroup></Project>";

            var files = new Dictionary<string, MockFileData> { [targetPath] = new(targetContent) };
            if (targetPath != projectPath)
            {
                files[projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" /></ItemGroup></Project>");
            }

            var fileSystem = new MockFileSystem(files);
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var first = service.Upgrade(projectPath, "Microsoft.Maui.Controls", "net8.0", new NuGetVersion("8.0.100"), noRestore);
            var second = service.Upgrade(projectPath, "Microsoft.Maui.Controls", "net9.0", new NuGetVersion("8.0.100"), noRestore);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
            dotNetRunner.Received(noRestore ? 0 : 1).Run(XFS.Path(@"c:\repo\src\MyProject"), Arg.Is<string[]>(a => a[0] == "restore" && a[1] == "MyProject.csproj"));

            var updated = fileSystem.File.ReadAllText(targetPath);
            Assert.Contains("<MauiVersion>8.0.100</MauiVersion>", updated);
            Assert.Contains($"{versionAttr}=\"$(MauiVersion)\"", updated);
        }

        [Fact]
        public void ImplicitVariableSharedAcrossMultiplePackages_InsertsPropertyOnceAndPreservesAllReferences()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string initialProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <UseMaui>true</UseMaui>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" />\n" +
                "    <PackageReference Include=\"Microsoft.Maui.Controls.Compatibility\" Version=\"$(MauiVersion)\" />\n  </ItemGroup>\n</Project>";

            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(initialProject) });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(call =>
            {
                var args = call.ArgAt<string[]>(1);
                var updated = Regex.Replace(
                    fileSystem.File.ReadAllText(projectPath),
                    $@"(<PackageReference\s+Include=""{Regex.Escape(args[3])}""\s+Version="")[^""]+("")",
                    $"${{1}}{args[5]}${{2}}");
                fileSystem.File.WriteAllText(projectPath, updated);
                return new RunStatus("", "", 0);
            });

            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            Assert.True(service.Upgrade(projectPath, "Microsoft.Maui.Controls", "net8.0", new NuGetVersion("8.0.100"), noRestore: false).IsSuccess);
            Assert.True(service.Upgrade(projectPath, "Microsoft.Maui.Controls.Compatibility", "net8.0", new NuGetVersion("8.0.100"), noRestore: false).IsSuccess);

            var finalContent = fileSystem.File.ReadAllText(projectPath);
            Assert.Single(Regex.Matches(finalContent, @"<MauiVersion>8\.0\.100</MauiVersion>"));
            Assert.Contains("Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\"", finalContent);
            Assert.Contains("Include=\"Microsoft.Maui.Controls.Compatibility\" Version=\"$(MauiVersion)\"", finalContent);
        }

        [Fact]
        public void SharedVariableAcrossProjects_IsWrittenOnceAndRestoresEachProjectOnce()
        {
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectA = XFS.Path(@"c:\repo\A\A.csproj");
            var projectB = XFS.Path(@"c:\repo\B\B.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new("<Project><PropertyGroup><V>8.0.3</V></PropertyGroup><ItemGroup><GlobalPackageReference Include=\"Analyzers\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectA] = new("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
                [projectB] = new("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Analyzers",
                Version = new NuGetVersion("8.0.100"),
                Projects =
                [
                    new UpgradeProject(projectA, "net8.0"), new UpgradeProject(projectA, "net9.0"),
                    new UpgradeProject(projectB, "net8.0"), new UpgradeProject(projectB, "net9.0"),
                ],
            });

            Assert.Equal([(projectA, UpgradeOutcome.Upgraded), (projectB, UpgradeOutcome.Upgraded)], results.Select(r => (r.ProjectPath, r.Outcome)));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\B"), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.ReceivedWithAnyArgs(2).Run(default, default);
            Assert.Contains("<V>8.0.100</V>", fileSystem.File.ReadAllText(packagesPath));
        }

        [Fact]
        public void SharedVariableAcrossUpgrades_RestoresAProjectNotYetRestored()
        {
            // The CLI makes one upgrade per resolved version, so projects that resolved different versions of the same
            // package are upgraded separately: the second finds the location written but must still restore its project.
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectA = XFS.Path(@"c:\repo\A\A.csproj");
            var projectB = XFS.Path(@"c:\repo\B\B.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new("<Project><PropertyGroup><V>8.0.3</V></PropertyGroup><ItemGroup><GlobalPackageReference Include=\"Analyzers\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectA] = new(SdkProjectContent),
                [projectB] = new(SdkProjectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var first = service.Upgrade(projectA, "Analyzers", "net8.0", new NuGetVersion("8.0.100"));
            var second = service.Upgrade(projectB, "Analyzers", "net8.0", new NuGetVersion("8.0.100"));

            Assert.Equal(UpgradeOutcome.Upgraded, first.Outcome);
            Assert.Equal(UpgradeOutcome.Upgraded, second.Outcome);
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\B"), Arg.Is<string[]>(a => a[0] == "restore"));
        }

        [Fact]
        public void ALocationChangedBackSinceAnUpgrade_RestoresItsProjectsWhenWrittenAgain()
        {
            // An upgrader outlives one upgrade (the MCP server keeps one), and the file can change between two:
            // what was restored at the version before says nothing about the version written now.
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectA = XFS.Path(@"c:\repo\A\A.csproj");
            var projectB = XFS.Path(@"c:\repo\B\B.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new(GlobalPackageReferenceContent),
                [projectA] = new(SdkProjectContent),
                [projectB] = new(SdkProjectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);
            var upgrade = new ReferenceUpgrade
            {
                Name = "SomeAnalyzer",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectA, "net8.0"), new UpgradeProject(projectB, "net8.0")],
            };

            service.Upgrade(upgrade);
            fileSystem.File.WriteAllText(packagesPath, GlobalPackageReferenceContent);
            var results = service.Upgrade(upgrade);

            Assert.All(results, result => Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome));
            Assert.Equal(GlobalPackageReferenceContent.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(packagesPath));
            dotNetRunner.Received(2).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.Received(2).Run(XFS.Path(@"c:\repo\B"), Arg.Is<string[]>(a => a[0] == "restore"));
        }

        [Fact]
        public void ALocationSetBackToAnEarlierVersionByHand_IsAlreadyCurrent()
        {
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new(GlobalPackageReferenceContent),
                [projectPath] = new(SdkProjectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            service.Upgrade(projectPath, "SomeAnalyzer", "net8.0", new NuGetVersion("2.0.0"));
            service.Upgrade(projectPath, "SomeAnalyzer", "net8.0", new NuGetVersion("3.0.0"));
            fileSystem.File.WriteAllText(packagesPath, GlobalPackageReferenceContent.Replace("1.0.0", "2.0.0"));
            var result = service.Upgrade(projectPath, "SomeAnalyzer", "net8.0", new NuGetVersion("2.0.0"));

            // This upgrader last wrote 3.0.0 there; 2.0.0 is someone else's, as if it had never written it.
            Assert.Equal(UpgradeOutcome.AlreadyCurrent, result.Outcome);
            dotNetRunner.Received(2).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "restore"));
        }

        [Fact]
        public void ALocationAlreadyAtTheVersion_IsAlreadyCurrentAndNotRestored()
        {
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new("<Project><PropertyGroup><V>8.0.100</V></PropertyGroup><ItemGroup><GlobalPackageReference Include=\"Analyzers\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectPath] = new(SdkProjectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Analyzers", "net8.0", new NuGetVersion("8.0.100"));

            Assert.Equal(UpgradeOutcome.AlreadyCurrent, result.Outcome);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void AProjectListedOncePerTargetFramework_RunsDotNetAddForEachOfThem()
        {
            // `dotnet add package -f` changes one target framework's reference: a reference declared per target
            // framework, or a transitive package being promoted, needs a run for each.
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net8.0"), new UpgradeProject(projectPath, "net9.0")],
            });

            Assert.Equal(UpgradeOutcome.Upgraded, Assert.Single(results).Outcome);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[6] == "-f" && a[7] == "net8.0"));
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[6] == "-f" && a[7] == "net9.0"));
            dotNetRunner.ReceivedWithAnyArgs(2).Run(default, default);
        }

        [Theory]
        [InlineData("net8.0", "8.0.5", "[8.0.5, 9.0.0)")]
        [InlineData("net9.0", "9.0.3", "[9.0.3, 10.0.0)")]
        // Outside this target framework's range, though inside the other's.
        [InlineData("net8.0", "9.0.3", "9.0.3")]
        public void AReferenceDeclaredPerTargetFramework_KeepsThatTargetFrameworksUpperBound(string targetFramework, string version, string expectedArgument)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Foo" Version="[8.0.0, 9.0.0)" /></ItemGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'"><PackageReference Include="Foo" Version="[9.0.0, 10.0.0)" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Foo", targetFramework, NuGetVersion.Parse(version));

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            dotNetRunner.Received(1).Run(
                Arg.Any<string>(),
                Arg.Is<string[]>(a => a[0] == "add" && a[5] == expectedArgument && a[7] == targetFramework));
        }

        [Fact]
        public void AHeldBackTargetFramework_WithItsOwnReference_LeavesTheOthersToBeUpgraded()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = Assert.Single(service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net10.0")],
                HeldBack = [new UpgradeProject(projectPath, "net8.0")],
            }));

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            Assert.Empty(result.Warnings);
            Assert.Empty(result.HeldBackUpgraded);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[7] == "net10.0"));
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[7] == "net8.0"));
        }

        // Leaving the shared reference behind could leave another package, upgraded for net10.0, depending on a newer
        // version than it, which restore rejects as a downgrade (NU1605).
        [Fact]
        public void AReferenceSharedWithAHeldBackTargetFramework_IsUpgradedForBoth_AndSaysSo()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
                      <ItemGroup><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = Assert.Single(service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net10.0")],
                HeldBack = [new UpgradeProject(projectPath, "net8.0")],
            }));

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            Assert.Equal([new UpgradeProject(projectPath, "net8.0")], result.HeldBackUpgraded);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[7] == "net10.0"));
        }

        [Fact]
        public void ACentralVersionSharedWithAHeldBackProject_IsUpgradedForBoth_AndSaysSo()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var oldProjectPath = XFS.Path(@"c:\repo\Old\Old.csproj");
            var newProjectPath = XFS.Path(@"c:\repo\New\New.csproj");
            const string projectContent = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Newtonsoft.Json" /></ItemGroup></Project>""";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [propsPath] = new(DirectoryPackagesPropsContent),
                [oldProjectPath] = new(projectContent),
                [newProjectPath] = new(projectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = Assert.Single(service.Upgrade(new ReferenceUpgrade
            {
                Name = "Newtonsoft.Json",
                Version = new NuGetVersion("13.0.1"),
                Projects = [new UpgradeProject(newProjectPath, "net10.0")],
                HeldBack = [new UpgradeProject(oldProjectPath, "net8.0")],
                NoRestore = true,
            }));

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            Assert.Equal([new UpgradeProject(oldProjectPath, "net8.0")], result.HeldBackUpgraded);
            Assert.Contains("Version=\"13.0.1\"", fileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void OnlyTheHeldBackTargetFrameworksSharingAnUpgradedReference_AreUpgradedWithIt()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks></PropertyGroup>
                      <ItemGroup Condition="'$(TargetFramework)' != 'net10.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = Assert.Single(service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net10.0")],
                HeldBack = [new UpgradeProject(projectPath, "net8.0"), new UpgradeProject(projectPath, "net9.0")],
            }));

            // net8.0 and net9.0 share a reference, but not with net10.0.
            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            Assert.Empty(result.HeldBackUpgraded);
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[7] == "net10.0"));
            dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);
        }

        [Fact]
        public void AProjectListedOncePerTargetFramework_KeepsEachTargetFrameworksUpperBound()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Foo" Version="[1.0.0, 3.0.0)" /></ItemGroup>
                      <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'"><PackageReference Include="Foo" Version="[1.0.0, 4.0.0)" /></ItemGroup>
                    </Project>
                    """),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net8.0"), new UpgradeProject(projectPath, "net9.0")],
            });

            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[5] == "[2.0.0, 3.0.0)" && a[7] == "net8.0"));
            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[5] == "[2.0.0, 4.0.0)" && a[7] == "net9.0"));
        }

        [Fact]
        public void AProjectListedOncePerTargetFramework_StopsAtTheFirstDotNetAddThatFails()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(SdkProjectContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "incompatible", 1));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net8.0"), new UpgradeProject(projectPath, "net9.0")],
            });

            var result = Assert.Single(results);
            Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
            Assert.Equal("incompatible", result.FailureDetails);
            dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);
        }

        [Theory]
        [InlineData("#:package Foo@[12.0.1, 13.0.0)", "[12.0.3, 13.0.0)")]
        [InlineData("#:package Foo@[12.0.1,13.0.0)", "[12.0.3, 13.0.0)")]
        // `dotnet add package` writes a range that holds a space in quotes.
        [InlineData("#:package Foo@\"[12.0.1, 13.0.0)\"", "[12.0.3, 13.0.0)")]
        [InlineData("#:package foo@[12.0.1, 13.0.0)", "[12.0.3, 13.0.0)")]
        [InlineData("#:package Foo@12.0.1", "12.0.3")]
        [InlineData("#:package Foo@[12.0.1, 12.0.2]", "12.0.3")]
        [InlineData("#:package Other@[12.0.1, 13.0.0)", "12.0.3")]
        public void FileBasedAppDirectPackage_KeepsTheDeclaredUpperBoundWhileTheVersionSatisfiesIt(string directive, string expectedArgument)
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [appPath] = new(directive + "\nConsole.WriteLine();\n"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(appPath, "Foo", "net10.0", NuGetVersion.Parse("12.0.3"));

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(
                Arg.Any<string>(),
                Arg.Is<string[]>(a => a[0] == "add" && a[4] == "-v" && a[5] == expectedArgument));
        }

        [Fact]
        public void NonSdkStyleProject_KeepsTheUpperBoundDeclaredByTheReferenceItRemoves()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project ToolsVersion=\"15.0\"><ItemGroup><PackageReference Include=\"Foo\" Version=\"[6.0.13, 7.0.0)\" /></ItemGroup></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(call =>
            {
                if (call.Arg<string[]>()[0] == "remove")
                {
                    // What `dotnet remove package` does.
                    fileSystem.File.WriteAllText(projectPath, "<Project ToolsVersion=\"15.0\"><ItemGroup /></Project>");
                }

                return new RunStatus("", "", 0);
            });
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Foo", "net48", NuGetVersion.Parse("6.0.36"));

            Assert.True(result.IsSuccess);
            Received.InOrder(() =>
            {
                dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "remove"));
                dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add" && a[5] == "[6.0.36, 7.0.0)"));
            });
        }

        [Fact]
        public void SharedVariableWrittenAfterDotNetAdd_RestoresAProjectThatHasItWrittenDirectly()
        {
            // A's reference is in its project file, so `dotnet add package` updates it and then the variable. B's is
            // in an imported file, so its variable is written directly: it is found written, and B must be restored.
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var referencesPath = XFS.Path(@"c:\repo\refs.props");
            var projectA = XFS.Path(@"c:\repo\A\A.csproj");
            var projectB = XFS.Path(@"c:\repo\B\B.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [propsPath] = new("<Project><PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup></Project>"),
                [referencesPath] = new("<Project><ItemGroup><PackageReference Include=\"Foo\" Version=\"$(FooVersion)\" /></ItemGroup></Project>"),
                [projectA] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Foo\" Version=\"$(FooVersion)\" /></ItemGroup></Project>"),
                [projectB] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"../refs.props\" /></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectA, "net8.0"), new UpgradeProject(projectB, "net8.0")],
            });

            Assert.Equal([(projectA, UpgradeOutcome.Upgraded), (projectB, UpgradeOutcome.Upgraded)], results.Select(r => (r.ProjectPath, r.Outcome)));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a[0] == "add"));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\B"), Arg.Is<string[]>(a => a[0] == "restore"));
            dotNetRunner.ReceivedWithAnyArgs(2).Run(default, default);
            Assert.Contains("<FooVersion>2.0.0</FooVersion>", fileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void AProjectThatFails_DoesNotStopTheOthers()
        {
            var projectA = XFS.Path(@"c:\repo\A\A.csproj");
            var projectB = XFS.Path(@"c:\repo\B\B.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectA] = new(SdkProjectContent),
                [projectB] = new(@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.0""></Project>"),
            });
            var service = new ReferenceUpgrader(Substitute.For<IDotNetRunner>(), fileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Microsoft.Build.NoTargets",
                Kind = ReferenceKind.Sdk,
                Version = new NuGetVersion("3.7.134"),
                Projects = [new UpgradeProject(projectA), new UpgradeProject(projectB)],
                NoRestore = true,
            });

            Assert.Equal([UpgradeOutcome.Failed, UpgradeOutcome.Upgraded], results.Select(r => r.Outcome));
            Assert.Contains("declares no versioned SDK", results[0].FailureDetails);
            Assert.Equal(@"<Project Sdk=""Microsoft.Build.NoTargets/3.7.134""></Project>", fileSystem.File.ReadAllText(projectB));
        }

        [Fact]
        public void UnsupportedVariableThatDotNetAddCannotUpdate_FailsWithoutChangingFiles()
        {
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            const string packagesContent = "<Project><PropertyGroup><Major>8</Major></PropertyGroup><ItemGroup><GlobalPackageReference Include=\"Analyzers\" Version=\"$(Major).0.3\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [packagesPath] = new(packagesContent),
                [projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var status = service.Upgrade(projectPath, "Analyzers", "net8.0", new NuGetVersion("9.0.0"), noRestore: false);

            Assert.False(status.IsSuccess);
            Assert.Contains("Update the version manually", status.FailureDetails);
            Assert.Equal(packagesContent, fileSystem.File.ReadAllText(packagesPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Fact]
        public void UnsupportedVariableInProjectFile_FallsBackToLiteralWithAWarning()
        {
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Major>8</Major></PropertyGroup>" +
                                    "<ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(Major).0.3\" /></ItemGroup></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(projectPath, "Serilog", "net8.0", new NuGetVersion("9.0.0"), noRestore: false);

            Assert.True(result.IsSuccess);
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Serilog"));
            var warning = Assert.Single(result.Warnings);
            Assert.Contains("literal version", warning);
            Assert.Contains("$(Major).0.3", warning);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void AddPackage_PassesIgnoreFailedSourcesToDotNet(bool noRestore, bool ignoreFailedSources)
        {
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Serilog\" Version=\"2.0.0\" /></ItemGroup></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            Assert.True(service.Upgrade(projectPath, "Serilog", "net8.0", new NuGetVersion("3.0.0"), noRestore, ignoreFailedSources).IsSuccess);

            List<string> expected = ["add", "A.csproj", "package", "Serilog", "-v", "3.0.0", "-f", "net8.0"];
            if (noRestore)
            {
                expected.Add("--no-restore");
            }

            if (ignoreFailedSources)
            {
                expected.Add("--ignore-failed-sources");
            }

            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a.SequenceEqual(expected)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Restore_PassesIgnoreFailedSourcesToDotNet(bool ignoreFailedSources)
        {
            var projectPath = XFS.Path(@"c:\repo\AppHost\AppHost.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project Sdk=\"Aspire.AppHost.Sdk/9.0.0\"></Project>"),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            Assert.True(service.Upgrade(projectPath, "Aspire.AppHost.Sdk", "net9.0", new NuGetVersion("9.1.0"), noRestore: false, ignoreFailedSources, kind: ReferenceKind.Sdk).IsSuccess);

            string[] expected = ignoreFailedSources
                ? ["restore", "AppHost.csproj", "--ignore-failed-sources"]
                : ["restore", "AppHost.csproj"];
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\AppHost"), Arg.Is<string[]>(a => a.SequenceEqual(expected)));
        }

        [Fact]
        public void DotNetRestoreService_RunsDotNetRestoreInTheProjectDirectory()
        {
            var projectPath = XFS.Path(@"c:\repo\A\A.csproj");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("restored", "", 0));
            var service = new DotNetRestoreService(dotNetRunner, new MockFileSystem());

            var status = service.Restore(projectPath);

            Assert.Equal("restored", status.Output);
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\A"), Arg.Is<string[]>(a => a.SequenceEqual(new[] { "restore", projectPath })));
        }

        [Fact]
        public void FileBasedAppVariableThatCannotBeWritten_ReportsFailureWithoutChangingFiles()
        {
            // The property's definition in Directory.Build.props depends on a condition that can't be evaluated
            // statically, so it is not known which definition to change.
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            const string appContent = "#:package Humanizer@$(HumanizerVersion)\nConsole.WriteLine();";
            const string propsContent = "<Project><PropertyGroup><HumanizerVersion Condition=\"Exists('missing.txt') And $([MSBuild]::IsOSPlatform('Windows'))\">2.14.1</HumanizerVersion></PropertyGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [appPath] = new(appContent),
                [propsPath] = new(propsContent),
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var status = service.Upgrade(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.0"), noRestore: false);

            Assert.False(status.IsSuccess);
            Assert.Equal(
                $"Failed to update file-based app '{appPath}' for 'Humanizer'. Could not update the matching #:property value or #:package/#:sdk directive.",
                status.FailureDetails);
            Assert.Equal(appContent, fileSystem.File.ReadAllText(appPath));
            Assert.Equal(propsContent, fileSystem.File.ReadAllText(propsPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        [Theory]
        [InlineData("#:sdk Cake.Sdk@6.0.0\n", "#:sdk Cake.Sdk@7.0.0\n")]
        [InlineData("#:property CakeVersion=6.0.0\n#:sdk Cake.Sdk@$(CakeVersion)\n", "#:property CakeVersion=7.0.0\n#:sdk Cake.Sdk@$(CakeVersion)\n")]
        public void FileBasedAppSdk_IsUpgradedInItsDirectiveOrProperty_MatchingTheIdIgnoringCase(string content, string expected)
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [appPath] = new(content) });
            var service = new ReferenceUpgrader(Substitute.For<IDotNetRunner>(), fileSystem);

            var result = service.Upgrade(appPath, "cake.sdk", "net10.0", new NuGetVersion("7.0.0"), noRestore: true, kind: ReferenceKind.Sdk);

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            Assert.Equal(expected, fileSystem.File.ReadAllText(appPath));
        }

        [Theory]
        [InlineData("#:package Cake.Sdk@6.0.0\n")]
        [InlineData("#:package Cake.Sdk@6.0.0\n#:sdk Other.Sdk@1.0.0\n")]
        public void FileBasedAppSdk_IsNotFoundInAPackageDirectiveWithTheSameId(string content)
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [appPath] = new(content) });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, fileSystem);

            var result = service.Upgrade(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("7.0.0"), kind: ReferenceKind.Sdk);

            Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
            Assert.Equal(
                $"Failed to update file-based app '{appPath}' for 'Cake.Sdk'. Expected a literal #:sdk Cake.Sdk@<version> directive " +
                "(id match is case-insensitive; trailing comments are preserved).",
                result.FailureDetails);
            Assert.Equal(content, fileSystem.File.ReadAllText(appPath));
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
        }

        // `dotnet add package` fails with "Cannot edit items in imported files" for a literal version declared in an
        // imported file, so the version is written there directly and the project restored.
        [Fact]
        public void ImportedPackageReference_UpdatesDirectoryBuildProps_WithoutCallingDotNetAdd()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryBuildPropsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Contoso.Widgets", "net8.0", new NuGetVersion("1.5.0"));

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
            dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\src\MyProject"), Arg.Is<string[]>(a => a.Length == 2 && a[0] == "restore" && a[1] == "MyProject.csproj"));
            Assert.Equal(
                DirectoryBuildPropsContent.Replace("Version=\"1.0.0\"", "Version=\"1.5.0\"", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void ImportedPackageReference_WithNoRestore_UpdatesDirectoryBuildPropsWithoutRunningDotNet()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryBuildPropsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Contoso.Gadgets", "net8.0", new NuGetVersion("2.1.0"), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(
                DirectoryBuildPropsContent.Replace("Version=\"2.0.0\"", "Version=\"2.1.0\"", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(propsPath));
        }

        // A shared file can pin a package separately for each target framework; only the pin the upgraded target
        // framework uses is written.
        [Theory]
        [InlineData("net6.0", "6.0.5", "8.0.0")]
        [InlineData("net8.0", "6.0.0", "8.0.5")]
        public void ImportedPackageReference_PinnedPerTargetFramework_UpdatesOnlyThatTargetFrameworksPin(
            string targetFramework, string expectedNet6, string expectedNet8)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string propsContent = """
                <Project>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net6.0'">
                    <PackageReference Include="Foo" Version="6.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Include="Foo" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """;
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net6.0;net8.0</TargetFrameworks></PropertyGroup></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var version = targetFramework == "net6.0" ? "6.0.5" : "8.0.5";
            var result = service.Upgrade(projectPath, "Foo", targetFramework, new NuGetVersion(version), noRestore: true);

            Assert.Equal(UpgradeOutcome.Upgraded, result.Outcome);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(
                propsContent
                    .Replace("Version=\"6.0.0\"", $"Version=\"{expectedNet6}\"", StringComparison.Ordinal)
                    .Replace("Version=\"8.0.0\"", $"Version=\"{expectedNet8}\"", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(propsPath));
        }

        // Upgrading every target framework writes each one's own pin, and restores the project once per pin written.
        [Fact]
        public void ImportedPackageReference_PinnedPerTargetFramework_UpgradingEveryTargetFramework_UpdatesEachPin()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string propsContent = """
                <Project>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net6.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup>
                </Project>
                """;
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net6.0;net8.0</TargetFrameworks></PropertyGroup></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var results = service.Upgrade(new ReferenceUpgrade
            {
                Name = "Foo",
                Version = new NuGetVersion("2.0.0"),
                Projects = [new UpgradeProject(projectPath, "net6.0"), new UpgradeProject(projectPath, "net8.0")],
                NoRestore = true,
            });

            Assert.Equal(UpgradeOutcome.Upgraded, Assert.Single(results).Outcome);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(propsContent.Replace("Version=\"1.0.0\"", "Version=\"2.0.0\"", StringComparison.Ordinal), mockFileSystem.File.ReadAllText(propsPath));
        }

        // dotnet add package targets the imported item, not a project's Update override of it, and fails.
        [Fact]
        public void ImportedPackageReference_WhenProjectOverridesVersion_UpdatesCsproj_NotImportedProps()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Update=""Contoso.Widgets"" Version=""1.2.0"" />
  </ItemGroup>
</Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryBuildPropsContent) },
                { projectPath, new MockFileData(projectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Contoso.Widgets", "net8.0", new NuGetVersion("1.5.0"));

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceive().Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
            Assert.Equal(
                projectContent.Replace("Version=\"1.2.0\"", "Version=\"1.5.0\"", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(projectPath));
            Assert.Equal(DirectoryBuildPropsContent, mockFileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void PackageWithDirectIncludeInProject_CallsDotNetAddPackage_LeavesImportedPropsUntouched()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Contoso.Widgets"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryBuildPropsContent) },
                { projectPath, new MockFileData(projectContent) }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            service.Upgrade(projectPath, "Contoso.Widgets", "net8.0", new NuGetVersion("1.5.0"));

            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
            Assert.Equal(DirectoryBuildPropsContent, mockFileSystem.File.ReadAllText(propsPath));
        }

        [Fact]
        public void PackageNotDeclaredInAnyEditableFile_FallsThroughToDotNetAddPackage()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryBuildPropsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            service.Upgrade(projectPath, "Contoso.Unlisted", "net8.0", new NuGetVersion("1.5.0"));

            dotNetRunner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "add"));
            Assert.Equal(DirectoryBuildPropsContent, mockFileSystem.File.ReadAllText(propsPath));
        }

        // A version written as a <Version> child element is valid MSBuild, and is updated in place.
        [Fact]
        public void ImportedPackageReference_WithVersionChildElement_UpdatesDirectoryBuildProps()
        {
            var propsPath = XFS.Path(@"c:\repo\src\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            const string propsContent = @"<Project>
  <ItemGroup>
    <PackageReference Include=""Contoso.Widgets"">
      <Version>1.0.0</Version>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Contoso.Widgets", "net8.0", new NuGetVersion("1.5.0"), noRestore: true);

            Assert.True(result.IsSuccess, result.FailureDetails);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(
                propsContent.Replace("<Version>1.0.0</Version>", "<Version>1.5.0</Version>", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(propsPath));
        }

        // A version written as a <Version> child element is valid MSBuild and must be updated too.
        [Fact]
        public void CpmProjectWithNoRestore_UpdatesPackageVersionWithVersionChildElement()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            const string propsContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"">
      <Version>12.0.3</Version>
    </PackageVersion>
  </ItemGroup>
</Project>";
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(propsContent) },
                { projectPath, new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""Newtonsoft.Json"" /></ItemGroup></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(dotNetRunner, mockFileSystem);

            var result = service.Upgrade(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore: true);

            Assert.True(result.IsSuccess, result.FailureDetails);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Equal(
                propsContent.Replace("<Version>12.0.3</Version>", "<Version>13.0.1</Version>", StringComparison.Ordinal),
                mockFileSystem.File.ReadAllText(propsPath));
        }
    }
}
