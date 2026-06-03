using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
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
            mock.UpdatePackageVariable(default, default).ReturnsForAnyArgs(true);
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
                    VersionRange = FileBasedAppReferenceHelper.CreateExactVersionRange(new NuGetVersion("6.0.0"))
                }
            ]);

            var service = new DotNetPackageService(dotNetRunner, mockFileSystem, variableTracking);

            var result = service.AddPackage(appPath, "Cake.Sdk", "net10.0", new NuGetVersion("6.2.0"), noRestore: true);

            Assert.False(result.IsSuccess);
            Assert.Contains("literal #:sdk Cake.Sdk@<version>", result.Output);
            Assert.Contains("#:property", result.Output);
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
    }
}
