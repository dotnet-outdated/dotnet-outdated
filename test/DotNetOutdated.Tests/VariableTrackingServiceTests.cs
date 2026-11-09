using System.Collections.Generic;
using System.Linq;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class VariableTrackingServiceTests
    {
        [Theory]
        [InlineData("cs", "sdk", false)]
        [InlineData("cs", "sdk", true)]
        [InlineData("cs", "package", false)]
        [InlineData("cs", "package", true)]
        [InlineData("csproj", "package", true)]
        public void Discover_InParallel_PreservesCacheEntriesAndAllowsUpdates(string extension, string directive, bool useVariable)
        {
            const int projectCount = 256;
            var paths = new string[projectCount];
            var files = new Dictionary<string, MockFileData>();
            var versionExpression = useVariable ? "$(PackageVersion)" : "1.0.0";
            for (var i = 0; i < projectCount; i++)
            {
                paths[i] = XFS.Path($@"c:\repo\app{i}\app.{extension}");
                files.Add(paths[i], new MockFileData(extension == "cs"
                    ? $"#:property PackageVersion=1.0.0\n#:{directive} Test.Package@{versionExpression}\n"
                    : "<Project><PropertyGroup><PackageVersion>1.0.0</PackageVersion></PropertyGroup>" +
                      "<ItemGroup><PackageReference Include=\"Test.Package\" Version=\"$(PackageVersion)\" /></ItemGroup></Project>"));
            }

            var fileSystem = new MockFileSystem(files);
            var warnings = new List<string>();
            var service = new VariableTrackingService(fileSystem, warnings.Add);
            var cachedVariables = new Dictionary<string, PackageVariableInfo>[projectCount];

            for (var attempt = 0; attempt < 10; attempt++)
            {
                service.ClearCache();
                Parallel.For(0, projectCount, i =>
                {
                    if (extension == "cs")
                    {
                        Assert.Single(service.DiscoverFileBasedAppReferences(paths[i]));
                    }

                    cachedVariables[i] = service.DiscoverPackageVariables(paths[i]);
                    if (useVariable)
                    {
                        Assert.Equal("1.0.0", cachedVariables[i]["Test.Package"].VariableValue);
                    }
                    else
                    {
                        Assert.Empty(cachedVariables[i]);
                    }
                });

                for (var i = 0; i < projectCount; i++)
                {
                    Assert.Same(cachedVariables[i], service.DiscoverPackageVariables(paths[i]));
                }
            }

            if (useVariable)
            {
                Assert.True(service.TryUpdatePackageVariable(cachedVariables[0]["Test.Package"], new NuGetVersion("2.0.0")));
                Assert.Equal("2.0.0", service.DiscoverPackageVariables(paths[0])["Test.Package"].VariableValue);
            }
            else
            {
                var kind = directive == "sdk" ? FileBasedAppReferenceKind.Sdk : FileBasedAppReferenceKind.Package;
                Assert.True(service.UpdateFileBasedAppDirectReference(paths[0], "Test.Package", kind, new NuGetVersion("2.0.0")));
                Assert.Contains($"#:{directive} Test.Package@2.0.0", fileSystem.File.ReadAllText(paths[0]));
            }

            Assert.Empty(warnings);
            Assert.Same(cachedVariables[1], service.DiscoverPackageVariables(paths[1]));
            if (extension == "cs")
            {
                Assert.Equal(new NuGetVersion("2.0.0"), Assert.Single(service.DiscoverFileBasedAppReferences(paths[0])).ResolvedVersion);
            }
        }

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
        public void Update_DirectReference_WithVersionRange_WritesRangeToProperty()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");

            // Post-dotnet-add-package state: property still has the old range, ref has the literal new range
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <NewtonsoftJsonVersion>[12.0.1,14.0.0)</NewtonsoftJsonVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""[13.0.4, 14.0.0)"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Newtonsoft.Json",
                VariableName = "NewtonsoftJsonVersion",
                VariableValue = "[12.0.1,14.0.0)",
                FilePath = projectPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            Assert.True(service.TryUpdatePackageVariable(variableInfo, VersionRange.Parse("[13.0.4, 14.0.0)")));

            var content = mockFileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<NewtonsoftJsonVersion>[13.0.4, 14.0.0)</NewtonsoftJsonVersion>", content);
            Assert.Contains(@"Version=""$(NewtonsoftJsonVersion)""", content);
            Assert.DoesNotContain("[12.0.1,14.0.0)", content);
        }

        [Fact]
        public void Update_WithPlainVersionRange_WritesSameValueAsNuGetVersionOverload()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    projectPath, new MockFileData(@"<Project>
  <PropertyGroup>
    <SomeVersion>4.0.0.0</SomeVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Some.Package"" Version=""4.5.0.0"" />
  </ItemGroup>
</Project>")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = new PackageVariableInfo
            {
                PackageName = "Some.Package",
                VariableName = "SomeVersion",
                VariableValue = "4.0.0.0",
                FilePath = projectPath,
                PackageReferenceFilePath = projectPath,
                ElementType = "PackageReference"
            };

            service.UpdatePackageVariable(variableInfo, new VersionRange(new NuGetVersion("4.5.0.0")));

            Assert.Contains("<SomeVersion>4.5.0.0</SomeVersion>", mockFileSystem.File.ReadAllText(projectPath));
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
        public void Discover_FileBasedAppReferences_DiscoversDirectSdkWithTrailingComment()
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
            var reference = service.DiscoverFileBasedAppReferences(appPath).Single();

            Assert.Equal("Cake.Sdk", reference.Name);
            Assert.Equal(new NuGetVersion("6.0.0"), reference.ResolvedVersion);
            Assert.Equal(FileBasedAppReferenceKind.Sdk, reference.Kind);
        }

        [Fact]
        public void Update_FileBasedApp_VariableSdkVersion_PropertyNameCasingDiffersFromReference()
        {
            var appPath = XFS.Path(@"c:\repo\cake.cs");
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {
                    // The #:property name (sdkversion) differs in case from the $(SdkVersion) reference.
                    appPath, new MockFileData(@"#:property sdkversion=6.0.0
#:sdk Cake.Sdk@$(SdkVersion)
Information(""Outdated Sdk"");")
                }
            });

            var service = new VariableTrackingService(mockFileSystem);
            var variableInfo = service.DiscoverPackageVariables(appPath)["Cake.Sdk"];

            Assert.True(service.TryUpdatePackageVariable(variableInfo, new NuGetVersion("6.2.0")));

            var content = mockFileSystem.File.ReadAllText(appPath);
            Assert.Contains("#:property sdkversion=6.2.0", content);
            Assert.DoesNotContain("6.0.0", content);
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

        [Theory]
        [InlineData("MyProject.csproj", "<PropertyGroup>\n    <UseMaui>true</UseMaui>\n  </PropertyGroup>", "PackageReference")]
        [InlineData("MyProject.csproj", "", "PackageReference")]
        [InlineData("Directory.Build.props", "<PropertyGroup>\n    <UseMaui>true</UseMaui>\n  </PropertyGroup>", "PackageReference")]
        [InlineData("Directory.Packages.props", "<PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>", "PackageVersion")]
        public void DiscoverAndUpdate_ImplicitSdkVariable_InsertsPropertyAndPreservesReference(string fileName, string propertyGroup, string elementType)
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var targetPath = fileName == "MyProject.csproj" ? projectPath : XFS.Path($@"c:\repo\{fileName}");
            var targetContent = $"<Project>\n  {propertyGroup}\n  <ItemGroup>\n    <{elementType} Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" />\n  </ItemGroup>\n</Project>";

            var files = new Dictionary<string, MockFileData> { [targetPath] = new(targetContent) };
            if (targetPath != projectPath)
            {
                files[projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            }

            var fileSystem = new MockFileSystem(files);
            var service = new VariableTrackingService(fileSystem);

            var info = Assert.Contains("Microsoft.Maui.Controls", service.DiscoverPackageVariables(projectPath));
            Assert.Equal("MauiVersion", info.VariableName);
            Assert.Equal(string.Empty, info.VariableValue);
            Assert.Equal(targetPath, info.FilePath);

            fileSystem.File.WriteAllText(targetPath, targetContent.Replace("$(MauiVersion)", "8.0.100"));
            Assert.True(service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100")));

            var updated = fileSystem.File.ReadAllText(targetPath);
            Assert.Contains("<MauiVersion>8.0.100</MauiVersion>", updated);
            Assert.Contains("Version=\"$(MauiVersion)\"", updated);
            Assert.Equal("8.0.100", service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"].VariableValue);
        }

        [Theory]
        [InlineData(
            "<!-- <MauiVersion>8.0.3</MauiVersion> -->\n    <MauiVersion Condition=\"'$(MauiVersion)' == ''\" Label=\"Default\">8.0.3</MauiVersion>",
            "<!-- <MauiVersion>8.0.3</MauiVersion> -->\n    <MauiVersion Condition=\"'$(MauiVersion)' == ''\" Label=\"Default\">8.0.100</MauiVersion>")]
        [InlineData(
            "<mauiversion>8.0.3</mauiversion>",
            "<mauiversion>8.0.100</mauiversion>")]
        [InlineData(
            "<UseMaui>true</UseMaui>\n    <MauiVersion>8.0.0</MauiVersion>\n    <MauiVersion Condition=\"'$(UseMaui)' == 'true'\">8.0.3</MauiVersion>",
            "<UseMaui>true</UseMaui>\n    <MauiVersion>8.0.0</MauiVersion>\n    <MauiVersion Condition=\"'$(UseMaui)' == 'true'\">8.0.100</MauiVersion>")]
        public void DiscoverAndUpdate_PropertyGroupEdgeCases_UpdatesTargetPropertyAndPreservesMarkup(string beforeProps, string afterProps)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = $"<Project>\n  <PropertyGroup>\n    {beforeProps}\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" />\n  </ItemGroup>\n</Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal("8.0.3", info.VariableValue);

            fileSystem.File.WriteAllText(projectPath, original.Replace("Version=\"$(MauiVersion)\"", "Version=\"8.0.100\""));
            Assert.True(service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100")));

            var updated = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains(afterProps, updated);
            Assert.Contains("Version=\"$(MauiVersion)\"", updated);
        }

        [Fact]
        public void DiscoverAndUpdate_GroupLevelDefaultCondition_UpdatesPropertyAndPreservesVariable()
        {
            var buildPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var props = "<Project>\n  <PropertyGroup Condition=\"'$(MauiVersion)' == ''\">\n    <MauiVersion>8.0.3</MauiVersion>\n  </PropertyGroup>\n</Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [buildPropsPath] = new(props),
                [projectPath] = new("<Project><ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" /></ItemGroup></Project>")
            });

            var service = new VariableTrackingService(fileSystem);
            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Null(info.UnsupportedReason);
            Assert.Equal("8.0.3", info.VariableValue);
            Assert.Equal(buildPropsPath, info.FilePath);

            Assert.True(service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100")));
            Assert.Equal(props.Replace("8.0.3", "8.0.100"), fileSystem.File.ReadAllText(buildPropsPath));
        }

        [Fact]
        public void Discover_GroupConditionOnUnrelatedUndefinedProperty_RemainsAmbiguous()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><MauiVersion>8.0.3</MauiVersion></PropertyGroup>" +
                                    "<ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" /></ItemGroup></Project>")
            });

            var info = new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.NotNull(info.UnsupportedReason);
        }

        [Fact]
        public void DiscoverAndUpdate_ChainedVariablesAndImportedProps_UpdatesRootPropertyAndInvalidatesCache()
        {
            var buildPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var versionsPropsPath = XFS.Path(@"c:\repo\eng\Versions.props");
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [buildPropsPath] = new("<Project><Import Project=\"$(MSBuildThisFileDirectory)eng/Versions.props\" /></Project>"),
                [versionsPropsPath] = new("<Project><PropertyGroup><SharedMauiVersion>8.0.3</SharedMauiVersion></PropertyGroup></Project>"),
                [projectPath] = new("<Project><PropertyGroup><MauiVersion>$(SharedMauiVersion)</MauiVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" /></ItemGroup></Project>")
            });

            var service = new VariableTrackingService(fileSystem);
            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal("SharedMauiVersion", info.VariableName);
            Assert.Equal("8.0.3", info.VariableValue);
            Assert.Equal(versionsPropsPath, info.FilePath);
            Assert.Equal("$(MauiVersion)", info.PackageReferenceVersion);

            fileSystem.File.WriteAllText(projectPath, fileSystem.File.ReadAllText(projectPath).Replace("Version=\"$(MauiVersion)\"", "Version=\"8.0.100\""));
            Assert.True(service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100")));

            Assert.Contains("<SharedMauiVersion>8.0.100</SharedMauiVersion>", fileSystem.File.ReadAllText(versionsPropsPath));
            var updatedProject = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<MauiVersion>$(SharedMauiVersion)</MauiVersion>", updatedProject);
            Assert.Contains("Version=\"$(MauiVersion)\"", updatedProject);
            Assert.Equal("8.0.100", service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"].VariableValue);
        }

        [Fact]
        public void Discover_CircularPropertyChainAndPropertyFunctions_HandledSafely()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("<Project><PropertyGroup><PropA>$(PropB)</PropA><PropB>$(PropA)</PropB></PropertyGroup>" +
                                    "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"$(PropA)\" />" +
                                    "<PackageReference Include=\"Serilog\" Version=\"$([System.String]::Copy('2.10.0'))\" /></ItemGroup></Project>")
            });

            var discovered = new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath);
            Assert.Contains("Newtonsoft.Json", discovered);
            Assert.NotNull(Assert.Contains("Serilog", discovered).UnsupportedReason);
        }

        [Theory]
        [InlineData("<PackageReference Include=\"Microsoft.Maui.Controls\"><Version>{0}</Version></PackageReference>", "$(MauiVersion)", "8.0.100", false)]
        [InlineData("<PackageReference Include=\"Microsoft.Maui.Controls\" VersionOverride=\"{0}\" />", "$(MauiVersion)", "8.0.100", true)]
        [InlineData("<PackageReference Include=\"Microsoft.Maui.Controls\"><VersionOverride>{0}</VersionOverride></PackageReference>", "$(MauiVersion)", "8.0.100", true)]
        [InlineData("<PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"{0}\" />", "[$(MauiVersion), 9.0.0)", "[8.0.100, 9.0.0)", false)]
        public void DiscoverAndUpdate_AlternativeVersionSyntaxAndRanges_UpdatesPropertyAndRestoresExpression(
            string itemTemplate, string originalExpr, string cliOverwrittenValue, bool expectVersionOverride)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var originalElement = string.Format(itemTemplate, originalExpr);
            var original = $"<Project><PropertyGroup><MauiVersion>8.0.3</MauiVersion></PropertyGroup><ItemGroup>{originalElement}</ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal(expectVersionOverride, info.IsVersionOverride);
            Assert.Equal(originalExpr, info.PackageReferenceVersion);

            fileSystem.File.WriteAllText(projectPath, original.Replace(originalElement, string.Format(itemTemplate, cliOverwrittenValue)));
            Assert.True(service.TryUpdatePackageVariable(info, VersionRange.Parse(cliOverwrittenValue)));

            var updated = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<MauiVersion>8.0.100</MauiVersion>", updated);
            Assert.Contains(originalElement, updated);
        }

        [Fact]
        public void Discover_IgnoresPropsFilesThatAreNotImported()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [XFS.Path(@"c:\repo\Legacy.props")] = new("<Project><PropertyGroup><V>1.0.0</V></PropertyGroup><ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            });

            Assert.Empty(new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath));
        }

        [Fact]
        public void Discover_LaterLiteralUpdateOverridesVariableFromDirectoryBuildProps()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [XFS.Path(@"c:\repo\Directory.Build.props")] = new("<Project><PropertyGroup><V>1.0.0</V></PropertyGroup><ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectPath] = new("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Update=\"Serilog\" Version=\"2.0.0\" /></ItemGroup></Project>"),
            });

            Assert.DoesNotContain("Serilog", new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath));
        }

        [Fact]
        public void Update_OnlyChangesTheEffectiveDefinitionWhenEarlierOnesHaveTheSameValue()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project><PropertyGroup><V>8.0.3</V><Early>$(V)</Early><V>8.0.3</V></PropertyGroup>" +
                           "<ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            Assert.True(service.TryUpdatePackageVariable(service.DiscoverPackageVariables(projectPath)["Serilog"], new NuGetVersion("8.0.100")));

            Assert.Contains("<V>8.0.3</V><Early>$(V)</Early><V>8.0.100</V>", fileSystem.File.ReadAllText(projectPath));
        }

        [Fact]
        public void Update_ImplicitVariable_IsInsertedIntoAnUnconditionalTopLevelPropertyGroup()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project>\n" +
                           "  <Target Name=\"T\">\n    <PropertyGroup>\n      <X>1</X>\n    </PropertyGroup>\n  </Target>\n" +
                           "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">\n    <DebugType>full</DebugType>\n  </PropertyGroup>\n" +
                           "  <PropertyGroup>\n    <UseMaui>true</UseMaui>\n  </PropertyGroup>\n" +
                           "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" />\n  </ItemGroup>\n</Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            Assert.True(service.TryUpdatePackageVariable(service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"], new NuGetVersion("8.0.100")));

            var updated = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("  <PropertyGroup>\n    <UseMaui>true</UseMaui>\n    <MauiVersion>8.0.100</MauiVersion>\n  </PropertyGroup>", updated);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(updated, "<MauiVersion>"));
        }

        [Fact]
        public void Update_LowerBoundRange_DropsUpperBoundWhenTheNewVersionIsOutsideIt()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project><PropertyGroup><MauiVersion>8.0.3</MauiVersion></PropertyGroup>" +
                           "<ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"[$(MauiVersion), 9.0.0)\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);
            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];

            // Direct update (no `dotnet add package` beforehand): the expression itself must change.
            Assert.True(service.TryUpdatePackageVariable(info, VersionRange.Parse("9.0.100")));

            var updated = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("<MauiVersion>9.0.100</MauiVersion>", updated);
            Assert.Contains("Version=\"$(MauiVersion)\"", updated);
        }

        [Fact]
        public void Update_LowerBoundRange_KeepsAChangedUpperBound()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project><PropertyGroup><MauiVersion>8.0.3</MauiVersion></PropertyGroup>" +
                           "<ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"[$(MauiVersion), 9.0.0)\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);
            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];

            Assert.True(service.TryUpdatePackageVariable(info, VersionRange.Parse("[8.0.100, 10.0.0)")));

            Assert.Contains("Version=\"[$(MauiVersion), 10.0.0)\"", fileSystem.File.ReadAllText(projectPath));
        }

        [Theory]
        [InlineData("<TargetFrameworks>net8.0;net9.0</TargetFrameworks>", true)]
        [InlineData("<TargetFramework>net8.0</TargetFramework>", false)]
        public void Discover_ConditionalRedefinition_IsAmbiguousUnlessTheConditionCanBeEvaluated(string targetFramework, bool ambiguous)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = $"<Project><PropertyGroup>{targetFramework}<MauiVersion>8.0.3</MauiVersion>" +
                           "<MauiVersion Condition=\"'$(TargetFramework)' == 'net9.0'\">9.0.0</MauiVersion></PropertyGroup>" +
                           "<ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal(ambiguous, info.UnsupportedReason != null);

            var updated = service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100"));
            Assert.Equal(!ambiguous, updated);
            var content = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("'net9.0'\">9.0.0</MauiVersion>", content);
            Assert.Contains(ambiguous ? "<MauiVersion>8.0.3</MauiVersion>" : "<MauiVersion>8.0.100</MauiVersion>", content);
        }

        [Fact]
        public void Discover_LaterImportOverridesEarlierProjectDefinition()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var versionsPath = XFS.Path(@"c:\repo\Versions.props");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [versionsPath] = new("<Project><PropertyGroup><MauiVersion>8.0.3</MauiVersion></PropertyGroup></Project>"),
                [projectPath] = new("<Project><PropertyGroup><MauiVersion>8.0.0</MauiVersion></PropertyGroup><Import Project=\"Versions.props\" />" +
                                    "<ItemGroup><PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MauiVersion)\" /></ItemGroup></Project>"),
            });

            var info = new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];

            Assert.Equal(versionsPath, info.FilePath);
            Assert.Equal("8.0.3", info.VariableValue);
        }

        [Fact]
        public void Discover_DirectoryPackagesPropsOverridesDirectoryBuildProps()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject.csproj");
            var packagesPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [XFS.Path(@"c:\repo\Directory.Build.props")] = new("<Project><PropertyGroup><V>1.0.0</V></PropertyGroup></Project>"),
                [packagesPath] = new("<Project><PropertyGroup><V>2.0.0</V></PropertyGroup><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>"),
                [projectPath] = new("<Project><ItemGroup><PackageReference Include=\"Serilog\" /></ItemGroup></Project>"),
            });

            var info = new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath)["Serilog"];

            Assert.Equal(packagesPath, info.FilePath);
            Assert.Equal("2.0.0", info.VariableValue);
        }

        [Fact]
        public void Discover_FollowsParentDirectoryBuildPropsImportedWithGetPathOfFileAbove()
        {
            var projectPath = XFS.Path(@"c:\repo\src\MyProject\MyProject.csproj");
            var rootPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [rootPropsPath] = new("<Project><PropertyGroup><V>1.0.0</V></PropertyGroup></Project>"),
                [XFS.Path(@"c:\repo\src\Directory.Build.props")] = new(
                    "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" /></Project>"),
                [projectPath] = new("<Project><ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>"),
            });

            var info = new VariableTrackingService(fileSystem).DiscoverPackageVariables(projectPath)["Serilog"];

            Assert.Equal(rootPropsPath, info.FilePath);
        }

        [Theory]
        [InlineData("$(MajorVersion).0.0")]
        [InlineData("[1.0.0, $(MaxVersion))")]
        public void Discover_UnsupportedVersionExpression_IsReportedAndNotUpdated(string expression)
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project><PropertyGroup><MajorVersion>2</MajorVersion><MaxVersion>3.0.0</MaxVersion></PropertyGroup>" +
                           $"<ItemGroup><PackageReference Include=\"Serilog\" Version=\"{expression}\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            var info = service.DiscoverPackageVariables(projectPath)["Serilog"];

            Assert.NotNull(info.UnsupportedReason);
            Assert.False(service.TryUpdatePackageVariable(info, new NuGetVersion("3.0.0")));
            Assert.Equal(original, fileSystem.File.ReadAllText(projectPath));
        }

        [Fact]
        public void Update_AliasOfSdkDefinedProperty_OverridesTheSdkPropertyBeforeTheAlias()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project>\n  <PropertyGroup>\n    <MyMauiVersion>$(MauiVersion)</MyMauiVersion>\n  </PropertyGroup>\n" +
                           "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Maui.Controls\" Version=\"$(MyMauiVersion)\" />\n  </ItemGroup>\n</Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var service = new VariableTrackingService(fileSystem);

            var info = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal("MauiVersion", info.VariableName);

            Assert.True(service.TryUpdatePackageVariable(info, new NuGetVersion("8.0.100")));

            var updated = fileSystem.File.ReadAllText(projectPath);
            Assert.Contains("    <MauiVersion>8.0.100</MauiVersion>\n    <MyMauiVersion>$(MauiVersion)</MyMauiVersion>", updated);
            Assert.Contains("Version=\"$(MyMauiVersion)\"", updated);

            var rediscovered = service.DiscoverPackageVariables(projectPath)["Microsoft.Maui.Controls"];
            Assert.Equal("MauiVersion", rediscovered.VariableName);
            Assert.Equal("8.0.100", rediscovered.VariableValue);
        }

        [Fact]
        public void Update_StaleDefinitionIndex_IsRefusedWithAWarning()
        {
            var projectPath = XFS.Path(@"c:\repo\MyProject.csproj");
            var original = "<Project><PropertyGroup><V>1.0.0</V></PropertyGroup><ItemGroup><PackageReference Include=\"Serilog\" Version=\"$(V)\" /></ItemGroup></Project>";
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(original) });
            var warnings = new List<string>();
            var service = new VariableTrackingService(fileSystem, warnings.Add);
            var info = service.DiscoverPackageVariables(projectPath)["Serilog"];

            // Someone else edits the property between discovery and update.
            var edited = original.Replace("<V>1.0.0</V>", "<V>1.5.0</V>");
            fileSystem.File.WriteAllText(projectPath, edited);

            Assert.False(service.TryUpdatePackageVariable(info, new NuGetVersion("2.0.0")));
            Assert.Equal(edited, fileSystem.File.ReadAllText(projectPath));
            Assert.Contains(warnings, w => w.Contains("changed since it was scanned"));
        }
    }
}
