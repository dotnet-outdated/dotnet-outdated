#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    /// <summary>
    /// The static MSBuild evaluation behind version variables: which files are imported, which property assignments
    /// take effect, and when an assignment is ambiguous because a condition can't be evaluated statically.
    /// </summary>
    public class MSBuildEvaluatorTests
    {
        private static readonly string RepoDirectory = XFS.Path(@"c:\repo");
        private static readonly string ProjectDirectory = XFS.Path(@"c:\repo\src\App");
        private static readonly string ProjectPath = XFS.Path(@"c:\repo\src\App\App.csproj");
        private static readonly string RootPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");

        private static string Project(string body) => $"""
            <Project Sdk="Microsoft.NET.Sdk">
            {body}
            </Project>
            """;

        private static string Props(string body) => $"""
            <Project>
            {body}
            </Project>
            """;

        private static MockFileSystem FileSystem(Dictionary<string, string> files)
        {
            var fileSystem = new MockFileSystem();
            foreach (var (path, content) in files)
            {
                fileSystem.AddFile(path, new MockFileData(content));
            }

            return fileSystem;
        }

        private static MSBuildEvaluator.EvaluationState Evaluate(Dictionary<string, string> files) =>
            new MSBuildEvaluator(FileSystem(files)).Evaluate(ProjectPath, includeProjectFile: true);

        private static MSBuildEvaluator.EvaluationState EvaluateProject(string projectBody, Dictionary<string, string>? otherFiles = null)
        {
            var files = new Dictionary<string, string>(otherFiles ?? []) { [ProjectPath] = Project(projectBody) };
            return Evaluate(files);
        }

        [Theory]
        [InlineData(null, true, false)]
        [InlineData("'a' == 'a'", true, false)]
        [InlineData("'a' == 'b'", false, false)]
        [InlineData("'$(Undefined)' == 'x'", true, true)]
        public void ImportGroup_FollowsItsImportsUnlessItsConditionIsFalse(string? condition, bool imported, bool ambiguous)
        {
            var conditionAttribute = condition == null ? string.Empty : $" Condition=\"{condition}\"";
            var state = EvaluateProject(
                $"""
                  <ImportGroup{conditionAttribute}>
                    <Import Project="Versions.props" />
                  </ImportGroup>
                """,
                new() { [Path.Combine(ProjectDirectory, "Versions.props")] = Props("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>") });

            Assert.Equal(imported, state.Properties.TryGetValue("FooVersion", out var definition));
            if (imported)
            {
                Assert.Equal("1.0.0", definition!.Value);
                Assert.Equal(ambiguous, definition.Ambiguous);
            }
        }

        [Theory]
        // The first true When wins; later branches aren't evaluated.
        [InlineData("<When Condition=\"'a' == 'a'\">1</When><Otherwise>2</Otherwise>", "1", false)]
        [InlineData("<When Condition=\"'a' == 'a'\">1</When><When Condition=\"'a' == 'a'\">3</When>", "1", false)]
        [InlineData("<When Condition=\"'a' == 'b'\">1</When><Otherwise>2</Otherwise>", "2", false)]
        [InlineData("<When Condition=\"'a' == 'b'\">1</When><When Condition=\"'b' == 'b'\">3</When><Otherwise>2</Otherwise>", "3", false)]
        [InlineData("<When Condition=\"'a' == 'b'\">1</When>", null, false)]
        // A When that can't be evaluated may or may not apply, so it and every later branch are ambiguous.
        [InlineData("<When Condition=\"'$(Undefined)' == 'x'\">1</When>", "1", true)]
        [InlineData("<When Condition=\"'$(Undefined)' == 'x'\">1</When><Otherwise>2</Otherwise>", "2", true)]
        [InlineData("<When Condition=\"'$(Undefined)' == 'x'\">1</When><When Condition=\"'a' == 'a'\">3</When><Otherwise>2</Otherwise>", "3", true)]
        public void Choose_AppliesTheFirstMatchingBranch(string branches, string? expectedValue, bool expectedAmbiguous)
        {
            // Each branch body is shorthand for the FooVersion it assigns.
            var expanded = System.Text.RegularExpressions.Regex.Replace(
                branches, @">(\d)<", ">\n<PropertyGroup><FooVersion>$1</FooVersion></PropertyGroup>\n<");

            var state = EvaluateProject($"<Choose>{expanded}</Choose>");

            if (expectedValue == null)
            {
                Assert.False(state.Properties.ContainsKey("FooVersion"));
                return;
            }

            var definition = state.Properties["FooVersion"];
            Assert.Equal(expectedValue, definition.Value);
            Assert.Equal(expectedAmbiguous, definition.Ambiguous);
        }

        [Fact]
        public void Property_ThatAliasesAnAmbiguousProperty_IsAmbiguous()
        {
            var state = EvaluateProject("""
                  <PropertyGroup>
                    <BaseVersion Condition="'$(Undefined)' == 'x'">1.0.0</BaseVersion>
                    <FooVersion>$(BaseVersion)</FooVersion>
                  </PropertyGroup>
                """);

            var definition = state.Properties["FooVersion"];
            Assert.Equal("BaseVersion", definition.LinkName);
            Assert.True(definition.Ambiguous);
        }

        [Theory]
        [InlineData("v$(BaseVersion)")]
        [InlineData("$(BaseVersion).1")]
        public void Property_ThatExpandsAnAmbiguousProperty_HasNoValue(string rawValue)
        {
            var state = EvaluateProject($"""
                  <PropertyGroup>
                    <BaseVersion Condition="'$(Undefined)' == 'x'">1.0.0</BaseVersion>
                    <FooVersion>{rawValue}</FooVersion>
                  </PropertyGroup>
                """);

            Assert.Null(state.Properties["FooVersion"].Value);
        }

        [Theory]
        [InlineData("x$(Undefined)")]
        [InlineData("$([System.String]::Copy('1.0.0'))")]
        [InlineData("@(Versions)")]
        [InlineData("%(Versions.Value)")]
        public void Property_ThatCantBeExpandedStatically_HasNoValue(string rawValue)
        {
            var state = EvaluateProject($"<PropertyGroup><FooVersion>{rawValue}</FooVersion></PropertyGroup>");

            Assert.Null(state.Properties["FooVersion"].Value);
            Assert.Equal(rawValue, state.Properties["FooVersion"].RawValue);
        }

        [Fact]
        public void DirectoryBuildTargets_IsEvaluatedAfterTheProject()
        {
            var state = Evaluate(new()
            {
                [ProjectPath] = Project("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>"),
                [XFS.Path(@"c:\repo\Directory.Build.targets")] = Props("<PropertyGroup><FooVersion>2.0.0</FooVersion></PropertyGroup>"),
            });

            Assert.Equal("2.0.0", state.Properties["FooVersion"].Value);
            Assert.Equal(XFS.Path(@"c:\repo\Directory.Build.targets"), state.Properties["FooVersion"].FilePath);
        }

        [Fact]
        public void Import_Cycle_IsEvaluatedOnce()
        {
            var aPath = Path.Combine(ProjectDirectory, "A.props");
            var bPath = Path.Combine(ProjectDirectory, "B.props");
            var state = EvaluateProject(
                """<Import Project="A.props" /><Import Project="A.props" />""",
                new()
                {
                    [aPath] = Props("""<PropertyGroup><Last>A</Last></PropertyGroup><Import Project="B.props" />"""),
                    [bPath] = Props("""<Import Project="A.props" /><PropertyGroup><Last>B</Last></PropertyGroup>"""),
                });

            // Project -> A -> B (whose import of A is skipped); the project's second import of A is skipped too.
            Assert.Equal("B", state.Properties["Last"].Value);
            Assert.Equal([ProjectPath, aPath, bPath], state.ImportedFiles);
        }

        [Fact]
        public void PackageVariables_OfAMissingProject_IsEmpty()
        {
            // Directory.Packages.props alone doesn't make a missing project's packages.
            var fileSystem = FileSystem(new()
            {
                [XFS.Path(@"c:\repo\Directory.Packages.props")] = Props("""<ItemGroup><PackageVersion Include="Foo" Version="$(FooVersion)" /></ItemGroup>"""),
            });

            Assert.Empty(new MSBuildEvaluator(fileSystem).PackageVariables(ProjectPath));
        }

        [Theory]
        [InlineData("Foo;Bar")]
        [InlineData("Foo; Bar")]
        [InlineData(" Foo ;; Bar ")]
        public void ItemRemove_RemovesEveryListedPackage(string remove)
        {
            var state = EvaluateProject($"""
                  <ItemGroup>
                    <PackageReference Include="Foo" Version="$(FooVersion)" />
                    <PackageReference Include="Bar" Version="$(BarVersion)" />
                    <PackageReference Include="Baz" Version="$(BazVersion)" />
                    <PackageReference Remove="{remove}" />
                  </ItemGroup>
                """);

            Assert.Equal(["Baz"], state.PackageReferences.Keys);
        }

        [Fact]
        public void Import_WithSdkAttribute_IsNotFollowed()
        {
            var state = EvaluateProject(
                """<Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />""",
                new() { [Path.Combine(ProjectDirectory, "Sdk.props")] = Props("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>") });

            Assert.False(state.Properties.ContainsKey("FooVersion"));
        }

        [Theory]
        [InlineData("*.props")]
        [InlineData("Versions?.props")]
        [InlineData(";Versions.props")]
        [InlineData("Common.props;Versions.props")]
        public void Import_OfAWildcardOrList_IsNotFollowedAsALiteralPath(string project)
        {
            // MSBuild treats these as globs or lists, which aren't resolved; a file literally named so isn't the import.
            var state = EvaluateProject(
                $"""<Import Project="{project}" />""",
                new() { [Path.Combine(ProjectDirectory, project)] = Props("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>") });

            Assert.False(state.Properties.ContainsKey("FooVersion"));
        }

        [Fact]
        public void Import_ByAbsolutePath_IsFollowed()
        {
            var versionsPath = XFS.Path(@"c:\shared\Versions.props");
            var state = EvaluateProject(
                $"""<Import Project="{versionsPath}" />""",
                new() { [versionsPath] = Props("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>") });

            Assert.Equal(versionsPath, state.Properties["FooVersion"].FilePath);
        }

        [Theory]
        [InlineData("marker.txt", true)]
        [InlineData("assets", true)]
        [InlineData("assets/marker.txt", true)]
        [InlineData(@"assets\marker.txt", true)]
        [InlineData("missing.txt", false)]
        [InlineData("root.txt", false)]
        [InlineData("", false)]
        [InlineData("  ", false)]
        public void ExistsCondition_ResolvesRelativePathsAgainstTheProjectDirectory(string path, bool exists)
        {
            // The condition is in the root Directory.Build.props, but as in MSBuild a relative Exists() path is
            // relative to the project, not to the file the condition is in.
            var state = Evaluate(new()
            {
                [ProjectPath] = Project(string.Empty),
                [RootPropsPath] = Props($"""<PropertyGroup><FooVersion Condition="Exists('{path}')">1.0.0</FooVersion></PropertyGroup>"""),
                [Path.Combine(ProjectDirectory, "marker.txt")] = string.Empty,
                [Path.Combine(ProjectDirectory, "assets", "marker.txt")] = string.Empty,
                [Path.Combine(RepoDirectory, "root.txt")] = string.Empty,
            });

            Assert.Equal(exists, state.Properties.TryGetValue("FooVersion", out var definition));
            Assert.False(definition?.Ambiguous ?? false);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExistsCondition_WithAbsolutePath_ChecksThatPath(bool exists)
        {
            var markerPath = XFS.Path(@"c:\elsewhere\marker.txt");
            var files = new Dictionary<string, string>
            {
                [ProjectPath] = Project($"""<PropertyGroup><FooVersion Condition="Exists('{markerPath}')">1.0.0</FooVersion></PropertyGroup>"""),
            };
            if (exists)
            {
                files[markerPath] = string.Empty;
            }

            var state = Evaluate(files);

            Assert.Equal(exists, state.Properties.ContainsKey("FooVersion"));
        }

        public static TheoryData<string, string> WellKnownProperties()
        {
            var sep = Path.DirectorySeparatorChar;
            return new TheoryData<string, string>
            {
                // The definitions are in the root Directory.Build.props, so "this file" isn't the project.
                { "MSBuildThisFileDirectory", RepoDirectory + sep },
                { "MSBuildThisFile", "Directory.Build.props" },
                { "MSBuildThisFileName", "Directory.Build" },
                { "MSBuildThisFileFullPath", RootPropsPath },
                { "MSBuildProjectDirectory", ProjectDirectory },
                { "MSBuildProjectFullPath", ProjectPath },
                { "MSBuildProjectFile", "App.csproj" },
                { "MSBuildProjectName", "App" },
                { "msbuildprojectname", "App" },
            };
        }

        [Theory]
        [MemberData(nameof(WellKnownProperties))]
        public void WellKnownProperty_ExpandsToItsPath(string name, string expected)
        {
            var state = Evaluate(new()
            {
                [ProjectPath] = Project(string.Empty),
                [RootPropsPath] = Props($"<PropertyGroup><Exact>$({name})</Exact><Embedded>[$({name})]</Embedded></PropertyGroup>"),
            });

            // A bare well-known property is expanded, not treated as an alias of a property defined elsewhere.
            Assert.Equal(expected, state.Properties["Exact"].Value);
            Assert.Null(state.Properties["Exact"].LinkName);
            Assert.Equal($"[{expected}]", state.Properties["Embedded"].Value);
        }

        [Theory]
        [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))")]
        [InlineData("$([msbuild]::getpathoffileabove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))")]
        [InlineData("$([MSBuild]::GetPathOfFileAbove( 'Directory.Build.props' , '$(MSBuildThisFileDirectory)..' ))")]
        // A relative start directory is relative to the importing file.
        [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '..'))")]
        public void Import_ThroughGetPathOfFileAbove_ImportsTheParentDirectoryBuildProps(string project)
        {
            var state = Evaluate(new()
            {
                [ProjectPath] = Project(string.Empty),
                [XFS.Path(@"c:\repo\src\Directory.Build.props")] = Props($"""<Import Project="{project}" />"""),
                [RootPropsPath] = Props("<PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup>"),
            });

            Assert.Equal(RootPropsPath, state.Properties["FooVersion"].FilePath);
        }

        [Theory]
        [InlineData("1.0.0", false)]
        [InlineData("$(Major).0.0", true)]
        [InlineData("@(Versions)", true)]
        [InlineData("%(Versions.Value)", true)]
        public void PackageVariables_WhenThePropertyIsAnExpression_IsUnsupported(string rawValue, bool unsupported)
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project($"""
                      <PropertyGroup>
                        <Major>1</Major>
                        <FooVersion>{rawValue}</FooVersion>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Foo" Version="$(FooVersion)" />
                      </ItemGroup>
                    """),
            });

            var info = new MSBuildEvaluator(fileSystem).PackageVariables(ProjectPath)["Foo"];

            Assert.Equal("FooVersion", info.VariableName);
            Assert.Equal(unsupported, info.UnsupportedReason != null);
            Assert.False(info.InsertProperty);
        }

        [Fact]
        public void PackageVariables_WhenThePropertyIsNotDefined_InsertsItNextToTheReference()
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project("""
                      <ItemGroup>
                        <PackageReference Include="Foo" Version="$(MauiVersion)" />
                      </ItemGroup>
                    """),
            });

            var info = new MSBuildEvaluator(fileSystem).PackageVariables(ProjectPath)["Foo"];

            Assert.True(info.InsertProperty);
            Assert.Equal(ProjectPath, info.FilePath);
            Assert.Null(info.UnsupportedReason);
        }

        [Fact]
        public void PackageVersions_ReportsTheFileAndEvaluatedRangeOfEachDeclaration()
        {
            var centralPath = XFS.Path(@"c:\repo\Directory.Packages.props");
            var fileSystem = FileSystem(new()
            {
                [centralPath] = Props("""
                    <PropertyGroup><BarVersion>2.0.0</BarVersion></PropertyGroup>
                    <ItemGroup>
                      <PackageVersion Include="Foo" Version="[1.0.0, 2.0.0)" />
                      <PackageVersion Include="Bar" Version="[$(BarVersion), 3.0.0)" />
                      <GlobalPackageReference Include="Analyzer" Version="1.5.0" />
                      <PackageVersion Include="Baz" Version="1.0.0" />
                    </ItemGroup>
                    """),
                [ProjectPath] = Project("""<ItemGroup><PackageReference Include="Baz" VersionOverride="1.2.0" /></ItemGroup>"""),
            });

            var versions = new MSBuildEvaluator(fileSystem).PackageVersions(ProjectPath);

            Assert.Equal(new PackageVersionDeclaration("PackageVersion", centralPath, "[1.0.0, 2.0.0)", false, NuGet.Versioning.VersionRange.Parse("[1.0.0, 2.0.0)"), null, ElementIndex: 0), versions["Foo"]);
            Assert.Equal(NuGet.Versioning.VersionRange.Parse("[2.0.0, 3.0.0)"), versions["Bar"].VersionRange);
            Assert.Equal("BarVersion", versions["Bar"].Variable?.VariableName);
            Assert.Equal(("GlobalPackageReference", centralPath), (versions["Analyzer"].ElementType, versions["Analyzer"].FilePath));
            // A version on the PackageReference takes precedence over the central one.
            Assert.Equal(("PackageReference", ProjectPath, true), (versions["Baz"].ElementType, versions["Baz"].FilePath, versions["Baz"].IsVersionOverride));
        }

        private const string PerTargetFrameworkItems = """
            <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
              <PackageReference Include="Foo" Version="[8.0.0, 9.0.0)" />
              <PackageReference Include="OnlyNet8" Version="1.0.0" />
            </ItemGroup>
            <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
              <PackageReference Include="Foo" Version="[9.0.0, 10.0.0)" />
            </ItemGroup>
            """;

        [Theory]
        [InlineData("net8.0", "[8.0.0, 9.0.0)")]
        [InlineData("net9.0", "[9.0.0, 10.0.0)")]
        // Without a target framework, or with one the project declares nothing for, the last item that may apply.
        [InlineData(null, "[9.0.0, 10.0.0)")]
        [InlineData("net10.0", "[9.0.0, 10.0.0)")]
        public void PackageVersions_OfAMultiTargetedProject_PrefersTheItemDeclaredForTheTargetFramework(string? targetFramework, string expected)
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project("<PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>" + PerTargetFrameworkItems),
            });

            var versions = new MSBuildEvaluator(fileSystem).PackageVersions(ProjectPath, targetFramework);

            Assert.Equal(expected, versions["Foo"].VersionExpression);
            // A package this target framework has no item for is still found where another declares it.
            Assert.Equal("1.0.0", versions["OnlyNet8"].VersionExpression);
        }

        // The index picks out the item a target framework uses among the file's items for the package, so a write
        // can leave the other target frameworks' items alone.
        [Theory]
        [InlineData("net8.0", 0)]
        [InlineData("net9.0", 1)]
        public void PackageVersions_OfAMultiTargetedProject_ReportsWhichOfTheFilesItemsIsUsed(string targetFramework, int expected)
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project("<PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>" + PerTargetFrameworkItems),
            });

            var versions = new MSBuildEvaluator(fileSystem).PackageVersions(ProjectPath, targetFramework);

            Assert.Equal(expected, versions["Foo"].ElementIndex);
            Assert.Equal(0, versions["OnlyNet8"].ElementIndex);
        }

        [Fact]
        public void PackageVersions_OfAProjectThatSetsItsTargetFramework_IgnoresTheOneAskedFor()
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project("<PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" + PerTargetFrameworkItems),
            });

            var versions = new MSBuildEvaluator(fileSystem).PackageVersions(ProjectPath, "net8.0");

            Assert.Equal("[9.0.0, 10.0.0)", versions["Foo"].VersionExpression);
            Assert.DoesNotContain("OnlyNet8", versions.Keys);
        }

        [Fact]
        public void PackageVersions_OfAVersionThatCantBeEvaluated_HasNoRange()
        {
            var fileSystem = FileSystem(new()
            {
                [ProjectPath] = Project("""<ItemGroup><PackageReference Include="Foo" Version="$(SdkDefinedVersion)" /></ItemGroup>"""),
            });

            var declaration = new MSBuildEvaluator(fileSystem).PackageVersions(ProjectPath)["Foo"];

            Assert.Null(declaration.VersionRange);
            Assert.NotNull(declaration.Variable);
        }
    }
}
