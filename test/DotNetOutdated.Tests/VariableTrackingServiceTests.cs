using System.Collections.Generic;
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
    }
}
