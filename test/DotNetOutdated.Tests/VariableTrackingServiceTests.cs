using System.Collections.Generic;
using System.Linq;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class VariableTrackingServiceTests
    {
        // ── DiscoverPackageVariables ──────────────────────────────────────────────

        [Fact]
        public void Discover_DirectReference_VariableDefinedAndUsedInSameCsproj()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            var info = Assert.Contains("Newtonsoft.Json", result);
            Assert.Equal("NewtonsoftJsonVersion", info.VariableName);
            Assert.Equal("12.0.3", info.VariableValue);
            Assert.Equal(projectPath, info.FilePath);
            Assert.Equal(projectPath, info.PackageReferenceFilePath);
            Assert.Equal("PackageReference", info.ElementType);
        }

        [Fact]
        public void Discover_CrossFile_VariableDefinedInPropsUsedInCsproj()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
</Project>")
                },
                {
                    projectPath, new MockFileData(@"<Project>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            var info = Assert.Contains("Newtonsoft.Json", result);
            Assert.Equal("NewtonsoftJsonVersion", info.VariableName);
            Assert.Equal("12.0.3", info.VariableValue);
            Assert.Equal(propsPath, info.FilePath);
            Assert.Equal(projectPath, info.PackageReferenceFilePath);
            Assert.Equal("PackageReference", info.ElementType);
        }

        [Fact]
        public void Discover_Cpvm_VariableDefinedAndUsedInDirectoryPackagesProps()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                },
                { projectPath, new MockFileData(@"<Project></Project>") }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            var info = Assert.Contains("Newtonsoft.Json", result);
            Assert.Equal("NewtonsoftJsonVersion", info.VariableName);
            Assert.Equal("12.0.3", info.VariableValue);
            Assert.Equal(propsPath, info.FilePath);
            Assert.Equal(propsPath, info.PackageReferenceFilePath);
            Assert.Equal("PackageVersion", info.ElementType);
        }

        [Fact]
        public void Discover_GlobalPackageReference_WithVariable()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <SomeAnalyzerVersion>1.0.0</SomeAnalyzerVersion>
  </PropertyGroup>
  <ItemGroup>
    <GlobalPackageReference Include=""SomeAnalyzer"" Version=""$(SomeAnalyzerVersion)"" />
  </ItemGroup>
</Project>")
                },
                { projectPath, new MockFileData(@"<Project></Project>") }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            var info = Assert.Contains("SomeAnalyzer", result);
            Assert.Equal("GlobalPackageReference", info.ElementType);
        }

        [Fact]
        public void Discover_NoVariableReferences_ReturnsEmpty()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.3"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            Assert.Empty(result);
        }

        [Fact]
        public void Discover_NonExistentProjectFile_ReturnsEmpty()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem();

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            Assert.Empty(result);
        }

        [Fact]
        public void Discover_FirstDefinitionWins_CsprojOverridesPropsFile()
        {
            // MSBuild: first definition wins. The project file is first in the scan list,
            // so its value should take precedence over Directory.Build.props.
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>10.0.0</NewtonsoftJsonVersion>
  </PropertyGroup>
</Project>")
                },
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(projectPath);

            var info = Assert.Contains("Newtonsoft.Json", result);
            Assert.Equal("12.0.3", info.VariableValue);
            Assert.Equal(projectPath, info.FilePath);
        }

        [Fact]
        public void Discover_ResultIsCached_StaleResultReturnedAfterFileChange()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);

            // First call populates cache
            var first = service.DiscoverPackageVariables(projectPath);
            Assert.Equal("12.0.3", first["Newtonsoft.Json"].VariableValue);

            // Modify file on disk
            mockFileSystem.File.WriteAllText(projectPath, @"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>13.0.1</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>");

            // Second call should return the cached (stale) value
            var second = service.DiscoverPackageVariables(projectPath);
            Assert.Equal("12.0.3", second["Newtonsoft.Json"].VariableValue);
        }

        [Fact]
        public void ClearCache_ForcesReread()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            service.DiscoverPackageVariables(projectPath);

            // Modify file, then clear cache
            mockFileSystem.File.WriteAllText(projectPath, @"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>13.0.1</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>");
            service.ClearCache();

            var result = service.DiscoverPackageVariables(projectPath);
            Assert.Equal("13.0.1", result["Newtonsoft.Json"].VariableValue);
        }

        // ── UpdatePackageVariable ─────────────────────────────────────────────────

        [Fact]
        public void Update_DirectReference_UpdatesPropertyAndRestoresVariableRef()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            // Post-dotnet-add-package state: property still has old version, ref has literal new version
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "12.0.3",
                FilePath = projectPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("13.0.1"));

            var content = mockFileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<NewtonsoftJsonVersion>13.0.1</NewtonsoftJsonVersion>", content);
            Assert.DoesNotContain("<NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>", content);
            Assert.Contains(@"Version=""$(NewtonsoftJsonVersion)""", content);
            Assert.DoesNotContain(@"Version=""13.0.1""", content);
        }

        [Fact]
        public void Update_CrossFile_UpdatesPropsAndRestoresVariableRefInCsproj()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            // Post-dotnet-add-package state: props still has old version, csproj has literal new version
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
</Project>")
                },
                {
                    projectPath, new MockFileData(@"<Project>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "12.0.3",
                FilePath = propsPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("13.0.1"));

            var propsContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("<NewtonsoftJsonVersion>13.0.1</NewtonsoftJsonVersion>", propsContent);
            Assert.DoesNotContain("<NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>", propsContent);

            var projectContent = mockFileSystem.File.ReadAllText(projectPath);
            Assert.Contains(@"Version=""$(NewtonsoftJsonVersion)""", projectContent);
            Assert.DoesNotContain(@"Version=""13.0.1""", projectContent);
        }

        [Fact]
        public void Update_Cpvm_UpdatesPropertyAndRestoresVariableRefInPackageVersion()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            // Post-dotnet-add-package state: property unchanged, PackageVersion has literal version
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>")
                },
                { projectPath, new MockFileData(@"<Project></Project>") }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "12.0.3",
                FilePath = propsPath,
                PackageReferenceFilePath = propsPath,
                ElementType = "PackageVersion"
            };

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("13.0.1"));

            var propsContent = mockFileSystem.File.ReadAllText(propsPath);
            Assert.Contains("<NewtonsoftJsonVersion>13.0.1</NewtonsoftJsonVersion>", propsContent);
            Assert.DoesNotContain("<NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>", propsContent);
            Assert.Contains(@"Version=""$(NewtonsoftJsonVersion)""", propsContent);
            Assert.DoesNotContain(@"Version=""13.0.1""", propsContent);
        }

        [Fact]
        public void Update_UnrelatedPackagesAreUntouched()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
    <SerilogVersion>2.10.0</SerilogVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""$(SerilogVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "12.0.3",
                FilePath = projectPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("13.0.1"));

            var content = mockFileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<SerilogVersion>2.10.0</SerilogVersion>", content);
            Assert.Contains(@"Version=""$(SerilogVersion)""", content);
        }

        [Fact]
        public void Update_WarningCallback_InvokedOnFailure()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { projectPath, new MockFileData("this is not valid xml <<<") }
            });

            var warnings = new System.Collections.Generic.List<string>();
            var service = new VariableTrackingService(mockFileSystem, warnings.Add);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "12.0.3",
                FilePath = projectPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("13.0.1"));

            var warning = Assert.Single(warnings);
            Assert.Contains("Newtonsoft.Json", warning);
        }

        [Fact]
        public void Update_InvalidatesCache_SubsequentDiscoverReadsNewValues()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""$(NewtonsoftJsonVersion)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);

            // Populate cache with pre-upgrade state
            var before = service.DiscoverPackageVariables(projectPath);
            Assert.Equal("12.0.3", before["Newtonsoft.Json"].VariableValue);

            // Simulate dotnet add package overwriting the version
            mockFileSystem.File.WriteAllText(projectPath, @"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>12.0.3</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>");

            // UpdatePackageVariable should write the new version and invalidate the cache
            service.UpdatePackageVariable(before["Newtonsoft.Json"], new NuGetVersion("13.0.1"));

            // Discover should re-read now and report the new version
            var after = service.DiscoverPackageVariables(projectPath);
            Assert.Equal("13.0.1", after["Newtonsoft.Json"].VariableValue);
        }

        [Fact]
        public void Discover_FileBasedApp_VariablePackageDirective()
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

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(appPath);

            var info = Assert.Contains("Humanizer", result);
            Assert.Equal("HumanizerPackageVersion", info.VariableName);
            Assert.Equal("3.0.10", info.VariableValue);
            Assert.Equal(appPath, info.FilePath);
            Assert.Equal(appPath, info.PackageReferenceFilePath);
            Assert.Equal(PackageVariableInfo.FileBasedPackageDirectiveElementType, info.ElementType);
            Assert.Equal("$(HumanizerPackageId)", info.PackageReferenceName);
            Assert.Equal("$(HumanizerPackageVersion)", info.PackageReferenceVersion);
        }

        [Fact]
        public void Update_FileBasedApp_UpdatesPropertyAndPreservesPackageDirectiveVariables()
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

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = service.DiscoverPackageVariables(appPath)["Humanizer"];

            service.UpdatePackageVariable(variableInfo, new NuGetVersion("3.0.11"));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property HumanizerPackageVersion=3.0.11", content);
            Assert.Contains("#:package $(HumanizerPackageId)@$(HumanizerPackageVersion)", content);
            Assert.DoesNotContain("3.0.10", content);
        }

        [Fact]
        public void Discover_FileBasedApp_PackageVersionVariableCanComeFromPropsFile()
        {
            var propsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    propsPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <HumanizerPackageVersion>3.0.10</HumanizerPackageVersion>
  </PropertyGroup>
</Project>")
                },
                {
                    appPath, new MockFileData(@"#!/usr/bin/env dotnet
#:package Humanizer@$(HumanizerPackageVersion)
Console.WriteLine();")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(appPath);

            var info = Assert.Contains("Humanizer", result);
            Assert.Equal(propsPath, info.FilePath);
            Assert.Equal("3.0.10", info.VariableValue);
        }

        [Fact]
        public void Discover_FileBasedApp_VariableSdkDirective()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:property CakeSdkVersion=6.0.0
#:sdk Cake.Sdk@$(CakeSdkVersion)
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverPackageVariables(appPath);

            var info = Assert.Contains("Cake.Sdk", result);
            Assert.Equal("CakeSdkVersion", info.VariableName);
            Assert.Equal("6.0.0", info.VariableValue);
            Assert.Equal(PackageVariableInfo.FileBasedSdkDirectiveElementType, info.ElementType);
            Assert.Equal("$(CakeSdkVersion)", info.PackageReferenceVersion);
        }

        [Fact]
        public void Discover_FileBasedApp_LaterPropertyDefinitionWinsWithinFile()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:property SdkVersion=6.0.0
#:property SdkVersion=6.2.0
#:sdk Cake.Sdk@$(SdkVersion)
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = Assert.Contains("Cake.Sdk", service.DiscoverPackageVariables(appPath));

            Assert.Equal("6.2.0", variableInfo.VariableValue);
            Assert.Equal("SdkVersion", variableInfo.VariableName);

            var reference = Assert.Single(service.DiscoverFileBasedAppReferences(appPath), r => r.VariableInfo != null);
            Assert.Equal(new NuGetVersion("6.2.0"), reference.ResolvedVersion);
        }

        [Fact]
        public void Discover_FileBasedAppReferences_IncludesDirectSdkDirective()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk Cake.Sdk@6.0.0
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var result = service.DiscoverFileBasedAppReferences(appPath).Single();

            Assert.Equal("Cake.Sdk", result.Name);
            Assert.Equal(new NuGetVersion("6.0.0"), result.ResolvedVersion);
            Assert.Equal(FileBasedAppReferenceKind.Sdk, result.Kind);
            Assert.Null(result.VariableInfo);
            Assert.Equal(result.ResolvedVersion, result.VersionRange.MinVersion);
            Assert.True(result.VersionRange.IsMinInclusive);
            Assert.Null(result.VersionRange.MaxVersion);
        }

        [Fact]
        public void Discover_FileBasedApp_SdkBeforePropertyDefinitions_ResolvesForwardReferences()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk $(SdkId)@$(SdkVersion)
#:property SdkId=Cake.Sdk
#:property SdkVersion=6.0.0
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = Assert.Contains("Cake.Sdk", service.DiscoverPackageVariables(appPath));
            Assert.Equal("SdkVersion", variableInfo.VariableName);
            Assert.Equal("6.0.0", variableInfo.VariableValue);

            var reference = Assert.Single(service.DiscoverFileBasedAppReferences(appPath), r => r.VariableInfo != null);
            Assert.Equal("Cake.Sdk", reference.Name);
            Assert.Equal(new NuGetVersion("6.0.0"), reference.ResolvedVersion);
            Assert.Equal(reference.ResolvedVersion, reference.VersionRange.MinVersion);
            Assert.Null(reference.VersionRange.MaxVersion);
        }

        [Fact]
        public void Update_FileBasedAppDirectSdk_UpdatesDirectiveLine()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk Cake.Sdk@6.0.0
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);

            Assert.True(service.UpdateFileBasedAppDirectReference(appPath, "Cake.Sdk", FileBasedAppReferenceKind.Sdk, new NuGetVersion("6.2.0")));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0", content);
        }

        [Fact]
        public void Update_FileBasedAppDirectSdk_PreservesTrailingContentOnDirectiveLine()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk Cake.Sdk@6.0.0 // pinned for now
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);

            Assert.True(service.UpdateFileBasedAppDirectReference(appPath, "Cake.Sdk", FileBasedAppReferenceKind.Sdk, new NuGetVersion("6.2.0")));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:sdk Cake.Sdk@6.2.0 // pinned for now", content);
        }

        [Fact]
        public void Update_FileBasedAppDirectSdk_ReturnsFalseWhenDirectiveDoesNotMatch()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:sdk $(SdkId)@6.0.0
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);

            Assert.False(service.UpdateFileBasedAppDirectReference(appPath, "Cake.Sdk", FileBasedAppReferenceKind.Sdk, new NuGetVersion("6.2.0")));
            Assert.Contains("#:sdk $(SdkId)@6.0.0", mockFileSystem.File.ReadAllText(appPath));
        }

        [Fact]
        public void Discover_FileBasedAppReferences_PrefersVariableBackedSdkEntry()
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

            var service = new VariableTrackingService(mockFileSystem);
            var reference = service.DiscoverFileBasedAppReferences(appPath).Single();

            Assert.Equal("Cake.Sdk", reference.Name);
            Assert.NotNull(reference.VariableInfo);
            Assert.Equal("$(SdkId)", reference.NameExpression);
            Assert.Equal("$(SdkVersion)", reference.VersionExpression);
            Assert.True(reference.UsesPropertyReferences);
        }

        [Fact]
        public void Discover_FileBasedAppReferences_PreservesDistinctPackageAndSdkWithSameName()
        {
            var appPath = XFS.Path(@"c:\repo\app.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:package SharedId@1.0.0
#:sdk SharedId@2.0.0
Console.WriteLine();")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var references = service.DiscoverFileBasedAppReferences(appPath);

            Assert.Equal(2, references.Count);
            Assert.Contains(references, reference =>
                reference.Kind == FileBasedAppReferenceKind.Package &&
                reference.Name == "SharedId" &&
                reference.ResolvedVersion == new NuGetVersion("1.0.0"));
            Assert.Contains(references, reference =>
                reference.Kind == FileBasedAppReferenceKind.Sdk &&
                reference.Name == "SharedId" &&
                reference.ResolvedVersion == new NuGetVersion("2.0.0"));
        }

        [Fact]
        public void GetDependencyDictionaryKey_DistinguishesPackageAndSdkWithSameName()
        {
            Assert.Equal(
                "SharedId#package",
                FileBasedAppReferenceHelper.GetDependencyDictionaryKey("SharedId", FileBasedAppReferenceKind.Package));
            Assert.Equal(
                "SharedId#sdk",
                FileBasedAppReferenceHelper.GetDependencyDictionaryKey("SharedId", FileBasedAppReferenceKind.Sdk));
            Assert.NotEqual(
                FileBasedAppReferenceHelper.GetDependencyDictionaryKey("SharedId", FileBasedAppReferenceKind.Package),
                FileBasedAppReferenceHelper.GetDependencyDictionaryKey("SharedId", FileBasedAppReferenceKind.Sdk));
        }

        [Fact]
        public void Update_FileBasedApp_VariableSdkIdAndVersion_PreservesExpressions()
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

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = service.DiscoverPackageVariables(appPath)["Cake.Sdk"];

            Assert.True(service.TryUpdatePackageVariable(variableInfo, new NuGetVersion("6.2.0")));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property SdkVersion=6.2.0", content);
            Assert.Contains("#:sdk $(SdkId)@$(SdkVersion)", content);
            Assert.DoesNotContain("#:property SdkVersion=6.0.0", content);
        }

        [Fact]
        public void Update_FileBasedApp_VariableSdkIdLiteralVersion_UpdatesDirectiveLine()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    appPath, new MockFileData(@"#:property SdkId=Cake.Sdk
#:sdk $(SdkId)@6.0.0
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = service.DiscoverPackageVariables(appPath)["Cake.Sdk"];

            Assert.True(service.TryUpdatePackageVariable(variableInfo, new NuGetVersion("6.2.0")));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:sdk $(SdkId)@6.2.0", content);
            Assert.DoesNotContain("#:sdk $(SdkId)@6.0.0", content);
        }
    }
}
