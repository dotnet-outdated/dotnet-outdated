using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class DotNetPackageServiceTests
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

        private const string GlobalPackageReferenceContent = @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <GlobalPackageReference Include=""SomeAnalyzer"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";

        private static IVariableTrackingService EmptyVariableTrackingService()
        {
            var mock = Substitute.For<IVariableTrackingService>();
            mock.DiscoverPackageVariables(default).ReturnsForAnyArgs([]);
            mock.DiscoverFileBasedAppReferences(default).ReturnsForAnyArgs([]);
            mock.TryUpdatePackageVariable(default, default).ReturnsForAnyArgs(true);
            mock.UpdateFileBasedAppDirectReference(default, default, default, default).ReturnsForAnyArgs(true);
            return mock;
        }

        [Fact]
        public void CpmProjectWithNoRestore_UpdatesDirectoryPackagesProps_DoesNotCallDotNet()
        {
            // Arrange
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act
            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
        public void CpmProjectWithoutNoRestore_CallsDotNetAddPackage()
        {
            // Arrange
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent) },
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act
            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act
            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                // dotnet add package reformats the props file while upgrading the version.
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1")
                    .Replace("  <ItemGroup>", "  <!-- Keep packages separate from properties. -->\n  <ItemGroup>"));
                return new RunStatus("", "", 0);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
            if (!noRestore)
            {
                dotNetRunner.Received(1).Run(fileSystem.Path.GetDirectoryName(projectPath),
                    Arg.Is<string[]>(args => string.Join(" ", args) == "add MyProject.csproj package Newtonsoft.Json -v 13.0.1 -f net8.0"));
            }
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
                { projectPath, new MockFileData("<Project></Project>") }
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
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var first = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore: false);
            var second = service.AddPackage(projectPath, "Serilog", "net8.0", new NuGetVersion("2.11.0"), noRestore: false);

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
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var updated = DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1")
                .Replace("</ItemGroup>", "  <PackageVersion Include=\"Other.Package\" Version=\"1.0.0\" />\n  </ItemGroup>");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, updated);
                return new RunStatus("", "", 0);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(updated, fileSystem.File.ReadAllText(propsPath));
        }

        [Theory]
        [InlineData("<PackageVersion Version='12.0.3' Include='Newtonsoft.Json' />")]
        [InlineData("<PackageVersion\n      Include = \"Newtonsoft.Json\"\n      Version = \"12.0.3\"\n    />")]
        [InlineData("<GlobalPackageReference Version=\"12.0.3\" Update=\"Newtonsoft.Json\" PrivateAssets=\"all\" />")]
        [InlineData("<PackageVersion Include=\"newtonsoft.json\" Version=\"12.0.3\"></PackageVersion>")]
        public void CpmProjectWithRestore_PreservesPackageAttributeFormatting(string packageElement)
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project>\n\n  <ItemGroup>\n    " + packageElement + "\n  </ItemGroup>\n\n</Project>\n";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(original) },
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath,
                    System.Xml.Linq.XDocument.Parse(original.Replace("12.0.3", "13.0.1")).ToString());
                return new RunStatus("", "", 0);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
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
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1"));
                return new RunStatus("", "", 0);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(original.Replace("12.0.3", "13.0.1"))];
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, fileSystem.File.ReadAllBytes(propsPath));
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
                { projectPath, new MockFileData("<Project></Project>") }
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
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
                { projectPath, new MockFileData("<Project></Project>") }
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
            var service = new DotNetPackageService(dotNetRunner, fileSystem, new VariableTrackingService(fileSystem));

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
                new NuGetVersion("13.0.1"), noRestore: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(original.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propsPath));
            if (propertyPath != propsPath)
            {
                Assert.Equal(propertyContent.Replace("12.0.3", "13.0.1"), fileSystem.File.ReadAllText(propertyPath));
            }
        }

        [Fact]
        public void CpmProjectWithRestore_FailedUpgradeDoesNotRestoreProps()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { propsPath, new MockFileData(DirectoryPackagesPropsContent + "\n\n") },
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var updated = DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, updated);
                return new RunStatus("", "Package is incompatible", 1);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act
            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0",
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
                { projectPath, new MockFileData("<Project></Project>") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(_ =>
            {
                fileSystem.File.WriteAllText(propsPath, DirectoryPackagesPropsContent.Replace("12.0.3", "13.0.1"));
                return new RunStatus("", "", 0);
            });
            var service = new DotNetPackageService(dotNetRunner, fileSystem, EmptyVariableTrackingService());

            var result = service.AddPackage(projectPath, "Newtonsoft.Json", "net8.0", new NuGetVersion("13.0.1"), noRestore);

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
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act
            var result = service.AddPackage(projectPath, "SomeAnalyzer", "net8.0",
                new NuGetVersion("2.0.0"), noRestore: true);

            // Assert
            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);

            var updatedContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("Version=\"2.0.0\"", updatedContent);
            Assert.DoesNotContain("Version=\"1.0.0\"", updatedContent);
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
                { projectPath, new MockFileData("<Project></Project>") }
            });

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, EmptyVariableTrackingService());

            // Act - package "UnknownPackage" is not in Directory.Packages.props
            var result = service.AddPackage(projectPath, "UnknownPackage", "net8.0",
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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: true);

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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: false);

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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Humanizer", "net10.0", new NuGetVersion("3.0.11"), noRestore: true);

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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0", content);
            Assert.DoesNotContain("#:sdk Cake.Sdk@6.0.0", content);
        }

        [Fact]
        public void FileBasedAppDirectSdkWithPropertyReferences_ReturnsFailureWithoutCallingDotNet()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { appPath, new MockFileData("#:sdk $(SdkId)@6.0.0") }
            });
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            var variableTracking = Substitute.For<IVariableTrackingService>();
            variableTracking.DiscoverPackageVariables(appPath).Returns([]);
            variableTracking.DiscoverFileBasedAppReferences(appPath).Returns(
            [
                new FileBasedAppReference
                {
                    Name = "Cake.Sdk",
                    Kind = FileBasedAppReferenceKind.Sdk,
                    NameExpression = "$(SdkId)",
                    VersionExpression = "6.0.0",
                    ResolvedVersion = new NuGetVersion("6.0.0"),
                    VersionRange = FileBasedAppReferenceHelper.CreateMinimumVersionRange(new NuGetVersion("6.0.0"))
                }
            ]);

            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, variableTracking);

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

            Assert.False(result.IsSuccess);
            Assert.Contains("Direct #:sdk updates require a literal id@version", result.Errors);
            Assert.Contains("#:property", result.Errors);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            variableTracking.DidNotReceive().UpdateFileBasedAppDirectReference(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<FileBasedAppReferenceKind>(),
                Arg.Any<NuGetVersion>());
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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

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
            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, new VariableTrackingService(mockFileSystem));

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

            Assert.True(result.IsSuccess);
            dotNetRunner.DidNotReceiveWithAnyArgs().Run(default, default);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0", mockFileSystem.File.ReadAllText(appPath));
        }
    }
}
