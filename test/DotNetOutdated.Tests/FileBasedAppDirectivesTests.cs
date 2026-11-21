#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    /// <summary>
    /// Reading and rewriting the <c>#:package</c>, <c>#:sdk</c> and <c>#:property</c> directives of a file-based app.
    /// </summary>
    public class FileBasedAppDirectivesTests
    {
        private static readonly string AppPath = XFS.Path(@"c:\repo\app.cs");
        private static readonly string RootPropsPath = XFS.Path(@"c:\repo\Directory.Build.props");
        private static readonly string TargetsPath = XFS.Path(@"c:\repo\Directory.Build.targets");

        private readonly List<string> _warnings = [];

        private FileBasedAppDirectives Directives(IFileSystem fileSystem, IProjectEvaluator? evaluator = null) =>
            new(fileSystem, evaluator ?? new DotNetProjectEvaluator(Substitute.For<IDotNetRunner>(), fileSystem), _warnings.Add);

        private static MockFileSystem FileSystem(string app, Dictionary<string, string>? otherFiles = null)
        {
            var fileSystem = new MockFileSystem();
            fileSystem.AddFile(AppPath, new MockFileData(app));
            foreach (var (path, content) in otherFiles ?? [])
            {
                fileSystem.AddFile(path, new MockFileData(content));
            }

            return fileSystem;
        }

        private static string Summary(IEnumerable<FileBasedAppReference> references) =>
            string.Join(", ", references.Select(r => $"{r.Kind} {r.Name}@{r.ResolvedVersion}"));

        [Theory]
        [InlineData(@"c:\repo\app.csproj", true)]
        [InlineData(@"c:\repo\app.txt", true)]
        [InlineData(@"c:\repo\missing.cs", false)]
        public void NotAnExistingCSharpFile_HasNoDirectivesAndIsNotEdited(string path, bool exists)
        {
            const string content = "#:property FooVersion=1.0.0\n#:package Foo@1.0.0\n#:package Bar@$(FooVersion)\n";
            var filePath = XFS.Path(path);
            var fileSystem = exists ? FileSystem(string.Empty, new() { [filePath] = content }) : FileSystem(string.Empty);
            var directives = Directives(fileSystem);

            Assert.Empty(directives.References(filePath));
            Assert.Empty(directives.PackageVariables(filePath, ReferenceKind.Package));
            Assert.NotNull(directives.WriteDirective(filePath, ReferenceKind.Package, "Foo", "2.0.0").Failure);
            if (exists)
            {
                Assert.Equal(content, fileSystem.File.ReadAllText(filePath));
            }
        }

        [Fact]
        public void References_DeclaredTwice_KeepsTheFirstDeclaration()
        {
            var fileSystem = FileSystem("#:package Foo@1.0.0\n#:package foo@2.0.0\n#:sdk Foo@3.0.0\n");

            var references = Directives(fileSystem).References(AppPath);

            Assert.Equal("Package Foo@1.0.0, Sdk Foo@3.0.0", Summary(references));
        }

        [Fact]
        public void References_SkipDirectivesWithoutAVersion()
        {
            // A versionless #:package takes its version from central package management; nothing to update here.
            var fileSystem = FileSystem("#:package Versionless\n#:package NoVersion@\n#:package @1.0.0\n#:sdk Sdk.Versionless\n#:package Foo@1.0.0\n");

            var directives = Directives(fileSystem);

            Assert.Equal("Package Foo@1.0.0", Summary(directives.References(AppPath)));
            Assert.Empty(directives.PackageVariables(AppPath, ReferenceKind.Package));
        }

        [Theory]
        [InlineData("#:package Foo@$(Undefined)")]
        [InlineData("#:package $(Undefined)@1.0.0")]
        [InlineData("#:package $(Undefined).Foo@1.0.0")]
        [InlineData("#:property FooId=Foo\n#:package $(FooId).$(Undefined)@1.0.0")]
        public void References_WithAnUndefinedProperty_AreSkipped(string app)
        {
            var directives = Directives(FileSystem(app + "\n#:package Bar@1.0.0\n"));

            Assert.Equal("Package Bar@1.0.0", Summary(directives.References(AppPath)));
            Assert.Empty(directives.PackageVariables(AppPath, ReferenceKind.Package));
        }

        [Theory]
        [InlineData("#:property BaseId=Foo\n#:property FooId=$(BaseId)\n#:package $(FooId)@2.0.0")]
        [InlineData("#:property Vendor=Fo\n#:property FooId=$(Vendor)o\n#:package $(FooId)@2.0.0")]
        [InlineData("#:property Major=2\n#:property FooVersion=$(Major).0.0\n#:package Foo@$(FooVersion)")]
        public void References_ResolveNestedProperties(string app)
        {
            var references = Directives(FileSystem(app + "\n")).References(AppPath);

            Assert.Equal("Package Foo@2.0.0", Summary(references));
        }

        [Theory]
        [InlineData("#:property A=$(A)\n#:package $(A)@1.0.0")]
        [InlineData("#:property A=$(B)\n#:property B=$(A)\n#:package $(A)@1.0.0")]
        [InlineData("#:property A=x$(A)\n#:package $(A)@1.0.0")]
        public void References_WithACircularProperty_AreSkipped(string app)
        {
            var references = Directives(FileSystem(app + "\n#:package Bar@1.0.0\n")).References(AppPath);

            Assert.Equal("Package Bar@1.0.0", Summary(references));
        }

        [Fact]
        public void Reference_VersionRange_IsTheVersionAndAbove()
        {
            var reference = Assert.Single(Directives(FileSystem("#:package Foo@1.2.3\n")).References(AppPath));

            Assert.Equal(new NuGetVersion("1.2.3"), reference.VersionRange.MinVersion);
            Assert.True(reference.VersionRange.IsMinInclusive);
            Assert.False(reference.VersionRange.HasUpperBound);
            Assert.False(reference.VersionRange.IsMaxInclusive);
        }

        [Fact]
        public void TryUpdateVariable_LeavesOtherPackagesDirectivesAlone()
        {
            const string app = """
                #:property FooVersion=1.0.0
                #:property BarId=Bar
                #:package Foo@$(FooVersion)
                #:package Bar@1.0.0
                #:package $(BarId).Extensions@1.0.0
                #:package Foo.Extensions@1.0.0

                """;
            var fileSystem = FileSystem(app);
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app.Replace("FooVersion=1.0.0", "FooVersion=2.0.0"), fileSystem.File.ReadAllText(AppPath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TryUpdateVariable_AlsoUpdatesADirectiveThatNamesThePackageThroughAProperty(bool idInDirectoryBuildProps)
        {
            // The same package declared again through a property-backed id, with a stale literal version: after the
            // update every directive for the package reads the variable.
            var app = (idInDirectoryBuildProps ? string.Empty : "#:property FooId=Foo\n") +
                "#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n#:package $(FooId)@1.0.0\n";
            var otherFiles = idInDirectoryBuildProps
                ? new Dictionary<string, string> { [RootPropsPath] = "<Project><PropertyGroup><FooId>Foo</FooId></PropertyGroup></Project>" }
                : null;
            var fileSystem = FileSystem(app, otherFiles);
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            var updated = fileSystem.File.ReadAllText(AppPath);
            Assert.Contains("#:property FooVersion=2.0.0\n", updated);
            Assert.DoesNotContain("@1.0.0", updated);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TryUpdateVariable_ToTheCurrentVersion_ChangesNothing(bool inDirectoryBuildProps)
        {
            var app = (inDirectoryBuildProps ? string.Empty : "#:property FooVersion=1.0.0\n") + "#:package Foo@$(FooVersion)\n";
            const string props = "<Project><PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(app, inDirectoryBuildProps ? new() { [RootPropsPath] = props } : null);
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("1.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            if (inDirectoryBuildProps)
            {
                Assert.Equal(props, fileSystem.File.ReadAllText(RootPropsPath));
            }

            Assert.Empty(_warnings);
        }

        [Fact]
        public void TryUpdateVariable_InDirectoryBuildProps_UpdatesTheProperty()
        {
            const string app = "#:package Foo@$(FooVersion)\n";
            var fileSystem = FileSystem(app, new() { [RootPropsPath] = "<Project><PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup></Project>" });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal("<Project><PropertyGroup><FooVersion>2.0.0</FooVersion></PropertyGroup></Project>", fileSystem.File.ReadAllText(RootPropsPath));
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void TryUpdateVariable_WhenTheDirectoryBuildPropsDefinitionIsConditional_WarnsAndChangesNothing()
        {
            const string app = "#:package Foo@$(FooVersion)\n";
            const string props = "<Project><PropertyGroup><FooVersion Condition=\"'$(Undefined)' == 'x'\">1.0.0</FooVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(app, new() { [RootPropsPath] = props });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(props, fileSystem.File.ReadAllText(RootPropsPath));
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Contains(_warnings, w => w.Contains("$(FooVersion)") && w.Contains("could not be determined"));
        }

        [Fact]
        public void TryUpdateVariable_WhenTheUpdateThrowsBeforeWritingAnything_WarnsAndReturnsFalse()
        {
            const string app = "#:package Foo@$(FooVersion)\n";
            const string props = "<Project><PropertyGroup><FooVersion>1.0.0</FooVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(app, new() { [RootPropsPath] = props });
            var variableInfo = Directives(fileSystem).PackageVariables(AppPath, ReferenceKind.Package)["Foo"];
            var evaluator = Substitute.For<IProjectEvaluator>();
            evaluator.GetImportedProperties(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("boom"));

            Assert.False(Directives(fileSystem, evaluator).TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(props, fileSystem.File.ReadAllText(RootPropsPath));
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            var warning = Assert.Single(_warnings);
            Assert.Contains("'Foo'", warning);
            Assert.Contains("boom", warning);
        }

        [Fact]
        public void TryUpdateVariable_WhenALaterFileCannotBeWritten_ChangesBackTheFilesAlreadyWrittenAndWarns()
        {
            // Every definition is written, or none: the SDK rejects the property declared with different values.
            const string app = "#:include lib/shared.cs\n#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            const string shared = "#:property FooVersion=1.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            fileSystem.File.SetAttributes(SharedPath, System.IO.FileAttributes.ReadOnly);
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            var write = directives.UpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0"));

            Assert.Empty(write.ChangedFiles);
            Assert.Contains($"'{SharedPath}' could not be written", write.Failure);
            Assert.Contains("No file was changed.", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));
            Assert.Contains(_warnings, w => w.Contains("No file was changed."));
        }

        private static readonly string SharedPath = XFS.Path(@"c:\repo\lib\shared.cs");
        private static readonly string NestedPath = XFS.Path(@"c:\repo\lib\nested.cs");

        private static string Relative(string path) =>
            path[(XFS.Path(@"c:\repo").Length + 1)..].Replace('\\', '/');

        [Fact]
        public void References_IncludeTheDirectivesOfIncludedFiles_RelativeToTheFileThatIncludesThem()
        {
            var fileSystem = FileSystem("#:include ./lib/shared.cs\n#:package Foo@1.0.0\n", new()
            {
                [SharedPath] = "#:package Bar@2.0.0\n#:include nested.cs\npublic static class Shared { }\n",
                [NestedPath] = "#:sdk Baz.Sdk@3.0.0\n",
            });

            var references = Directives(fileSystem).References(AppPath);

            Assert.Equal("Package Foo@1.0.0, Package Bar@2.0.0, Sdk Baz.Sdk@3.0.0", Summary(references));
            Assert.Equal(
                [AppPath, SharedPath, NestedPath],
                references.Select(reference => reference.FilePath));
        }

        [Fact]
        public void References_DeclaredInTheEntryPointAndAnIncludedFile_AreDeclaredOnceByTheEntryPoint()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Foo@1.0.0\n", new()
            {
                [SharedPath] = "#:package foo@1.0.0\n",
            });

            var reference = Assert.Single(Directives(fileSystem).References(AppPath));

            Assert.Equal(AppPath, reference.FilePath);
        }

        [Fact]
        public void References_OfAFileThatIsNotIncluded_AreNotTheApps()
        {
            var fileSystem = FileSystem("#:package Foo@1.0.0\n", new()
            {
                [SharedPath] = "#:package Bar@2.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal("Package Foo@1.0.0", Summary(directives.References(AppPath)));
            Assert.False(directives.IncludesFiles(AppPath));
        }

        [Fact]
        public void PackageVariables_ResolveAPropertyDefinedInAnotherFileTheAppCompiles()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Foo@$(FooVersion)\n", new()
            {
                [SharedPath] = "#:property FooVersion=1.2.3\n#:package Bar@$(BarVersion)\n#:property BarVersion=4.5.6\n",
            });

            var variables = Directives(fileSystem).PackageVariables(AppPath, ReferenceKind.Package);

            Assert.Equal((SharedPath, AppPath, "1.2.3"), (variables["Foo"].FilePath, variables["Foo"].PackageReferenceFilePath, variables["Foo"].VariableValue));
            Assert.Equal((SharedPath, SharedPath, "4.5.6"), (variables["Bar"].FilePath, variables["Bar"].PackageReferenceFilePath, variables["Bar"].VariableValue));
        }

        [Theory]
        [InlineData("#:include **/*.cs", "lib/a.cs, lib/nested.cs, lib/shared.cs, lib/sub/b.cs, my lib/c.cs, other.cs")]
        [InlineData("#:include ./*.cs", "other.cs")]
        [InlineData("#:include lib/*.cs", "lib/a.cs, lib/nested.cs, lib/shared.cs")]
        [InlineData("#:include lib/**/*.cs", "lib/a.cs, lib/nested.cs, lib/shared.cs, lib/sub/b.cs")]
        [InlineData("#:include lib/?.cs", "lib/a.cs")]
        // The item type comes from the path's extension, so a path without one names no Compile items.
        [InlineData("#:include lib/**", "")]
        [InlineData("#:include lib/*.cs\n#:property FileBasedProgramsItemMapping=.cs=None", "")]
        [InlineData("#:property FileBasedProgramsItemMapping=.txt=Compile;.cs=Compile\n#:include lib/*.txt", "lib/notes.txt")]
        // An unquoted path is taken whole unless what follows it is metadata, as by the SDK.
        [InlineData("#:include my lib/c.cs", "my lib/c.cs")]
        [InlineData("#:include lib/a.cs Link=a.cs", "lib/a.cs")]
        [InlineData("#:include $(MSBuildProjectDirectory)/lib/a.cs", "lib/a.cs")]
        [InlineData("#:include $(MSBuildThisFileDirectory)lib/a.cs", "lib/a.cs")]
        [InlineData("#:include **/*.cs\n#:exclude lib/shared.cs\n#:exclude lib/sub/*.cs", "lib/a.cs, lib/nested.cs, my lib/c.cs, other.cs")]
        [InlineData("#:include lib/a.cs\n#:include lib/a.cs\n#:include missing/*.cs\n#:include lib/notes.txt", "lib/a.cs")]
        [InlineData("#:include \"my lib/c.cs\" CopyToOutputDirectory=PreserveNewest", "my lib/c.cs")]
        [InlineData("#:property LibDir=lib\n#:include $(LibDir)/a.cs", "lib/a.cs")]
        // MSBuild splits an item's Include on ';'.
        [InlineData("#:property Lib=lib/a.cs;lib/shared.cs\n#:include $(Lib)", "lib/a.cs, lib/shared.cs")]
        [InlineData("#:include $(Undefined)/a.cs", "")]
        // A quoted property value is a C# string literal; one the SDK rejects defines nothing.
        [InlineData("#:property LibDir=\"lib\"\n#:include $(LibDir)/a.cs", "lib/a.cs")]
        [InlineData("#:property LibDir = \"my lib\"\n#:include \"$(LibDir)/c.cs\"", "my lib/c.cs")]
        [InlineData("#:property LibDir=\"l\\x69\\u0062\"\n#:include $(LibDir)/a.cs", "lib/a.cs")]
        [InlineData("#:property LibDir=\"l\\qb\"\n#:include $(LibDir)/a.cs", "")]
        [InlineData("#:property LibDir=\"lib\n#:include $(LibDir)/a.cs", "")]
        public void SourceFiles_AreTheEntryPointAndTheCSharpFilesItsIncludesMatch(string app, string expected)
        {
            var fileSystem = FileSystem(app + "\n", new()
            {
                [XFS.Path(@"c:\repo\other.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\lib\a.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\lib\notes.txt")] = string.Empty,
                [SharedPath] = string.Empty,
                [NestedPath] = string.Empty,
                [XFS.Path(@"c:\repo\lib\sub\b.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\my lib\c.cs")] = string.Empty,
            });

            var sourceFiles = Directives(fileSystem).SourceFiles(AppPath);

            Assert.Equal(AppPath, sourceFiles[0]);
            Assert.Equal(expected, string.Join(", ", sourceFiles.Skip(1).Select(Relative)));
        }

        [Theory]
        // The SDK writes the directives as ordered Include and Remove items.
        [InlineData("#:include lib/*.cs\n#:exclude lib/nested.cs", "", "lib/shared.cs")]
        [InlineData("#:exclude lib/nested.cs\n#:include lib/*.cs", "", "lib/nested.cs, lib/shared.cs")]
        [InlineData("#:include lib/*.cs\n#:exclude lib/nested.cs\n#:include lib/nested.cs", "", "lib/shared.cs, lib/nested.cs")]
        // An exclude in an included file removes a file not found yet, but one already found is still read, as by the SDK.
        [InlineData("#:include lib/shared.cs", "#:exclude nested.cs\n#:include *.cs\n#:exclude nested.cs", "lib/shared.cs")]
        [InlineData("#:include lib/*.cs", "#:exclude nested.cs", "lib/nested.cs, lib/shared.cs")]
        // The entry point is always compiled.
        [InlineData("#:include lib/shared.cs", "#:exclude ../app.cs", "lib/shared.cs")]
        public void SourceFiles_FollowIncludesAndExcludesInTheOrderTheAppReadsThem(string app, string shared, string expected)
        {
            var fileSystem = FileSystem(app + "\n", new()
            {
                [SharedPath] = shared + "\n",
                [NestedPath] = string.Empty,
            });

            var sourceFiles = Directives(fileSystem).SourceFiles(AppPath);

            Assert.Equal(AppPath, sourceFiles[0]);
            Assert.Equal(expected, string.Join(", ", sourceFiles.Skip(1).Select(Relative)));
        }

        [Fact]
        public void SourceFiles_IncludedThroughAnImportedProperty_UseItsEvaluatedValue()
        {
            // The SDK expands the include through MSBuild, so $(MSBuildThisFileDirectory) is the props file's directory.
            var fileSystem = FileSystem("#:include $(LibDir)/shared.cs\n", new()
            {
                [RootPropsPath] = "<Project><PropertyGroup><LibDir>$(MSBuildThisFileDirectory)lib</LibDir></PropertyGroup></Project>",
                [SharedPath] = "#:package Bar@2.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath, SharedPath], directives.SourceFiles(AppPath));
            Assert.Equal("Package Bar@2.0.0", Summary(directives.References(AppPath)));
        }

        [Fact]
        public void SourceFiles_IncludedThroughAnImportedProperty_SeeTheVirtualProjectsReservedProperties()
        {
            // The app's project is app.cs.csproj, so its name is app.cs.
            var includedPath = XFS.Path(@"c:\repo\app.cs.lib\shared.cs");
            var fileSystem = FileSystem("#:include $(LibDir)/shared.cs\n", new()
            {
                [RootPropsPath] = "<Project><PropertyGroup><LibDir>$(MSBuildProjectName).lib</LibDir></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\app.lib\shared.cs")] = "#:package Foo@1.0.0\n",
                [includedPath] = "#:package Bar@2.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath, includedPath], directives.SourceFiles(AppPath));
        }

        [Fact]
        public void SourceFiles_IncludedThroughAPropertyThatDirectoryBuildTargetsSets_UseItsValue()
        {
            // Directory.Build.targets is imported after the app's #:property lines, so its value wins, as for the SDK.
            var fileSystem = FileSystem("#:property Lib=first.cs\n#:include $(Lib)\n", new()
            {
                [TargetsPath] = "<Project><PropertyGroup><Lib>second.cs</Lib></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\first.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\second.cs")] = string.Empty,
            });

            Assert.Equal([AppPath, XFS.Path(@"c:\repo\second.cs")], Directives(fileSystem).SourceFiles(AppPath));
        }

        [Fact]
        public void SourceFiles_IncludedThroughAPropertyAssignedFromOneDirectoryBuildTargetsSetsLater_UseTheValueItHadThen()
        {
            // Lib takes Root's value when Lib is assigned; Directory.Build.targets sets Root only afterwards.
            var fileSystem = FileSystem("#:property Root=first\n#:property Lib=$(Root)\n#:include $(Lib)/shared.cs\n", new()
            {
                [TargetsPath] = "<Project><PropertyGroup><Root>second</Root></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\first\shared.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\second\shared.cs")] = string.Empty,
            });

            Assert.Equal([AppPath, XFS.Path(@"c:\repo\first\shared.cs")], Directives(fileSystem).SourceFiles(AppPath));
        }

        [Theory]
        // The default applies only when the app leaves Lib undefined, which this can't be sure of once it defines it.
        [InlineData("#:property Lib=first.cs\n#:include $(Lib)\n", "")]
        [InlineData("#:include $(Lib)\n", "second.cs")]
        public void SourceFiles_IncludedThroughADirectoryBuildTargetsDefault_FollowItOnlyWhenTheAppLeavesItUndefined(string app, string expected)
        {
            var fileSystem = FileSystem(app, new()
            {
                [TargetsPath] = "<Project><PropertyGroup><Lib Condition=\"'$(Lib)' == ''\">second.cs</Lib></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\first.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\second.cs")] = string.Empty,
            });
            var directives = Directives(fileSystem);

            Assert.Equal(expected, string.Join(", ", directives.SourceFiles(AppPath).Skip(1).Select(path => path[(XFS.Path(@"c:\repo").Length + 1)..])));
            if (expected.Length == 0)
            {
                Assert.Contains("could not be followed", directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0").Failure);
            }
        }

        [Fact]
        public void SourceFiles_IncludedThroughADirectoryBuildTargetsDefault_KeepAFileThatRedefinesItAfterward()
        {
            // shared.cs was included while Lib had its default; its own Lib only affects includes read after it.
            const string shared = "#:property Lib=another.cs\n#:package Foo@1.0.0\n";
            var sharedPath = XFS.Path(@"c:\repo\shared.cs");
            var fileSystem = FileSystem("#:include $(Lib)\n#:package Foo@1.0.0\n", new()
            {
                [TargetsPath] = "<Project><PropertyGroup><Lib Condition=\"'$(Lib)' == ''\">shared.cs</Lib></PropertyGroup></Project>",
                [sharedPath] = shared,
            });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath, sharedPath], directives.SourceFiles(AppPath));
            Assert.Equal([AppPath, sharedPath], directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0").ChangedFiles);
        }

        [Fact]
        public void SourceFiles_IncludedThroughADirectoryBuildTargetsPropertyThatUsesAnother_AreNotFollowed()
        {
            // Its value was evaluated without the app's #:property lines, so the file it names isn't certain.
            var fileSystem = FileSystem("#:property Name=second\n#:include $(Lib)\n#:package Foo@1.0.0\n", new()
            {
                [TargetsPath] = "<Project><PropertyGroup><Lib>$(Name).cs</Lib></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\second.cs")] = "#:package Foo@1.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath], directives.SourceFiles(AppPath));
            Assert.Contains("could not be followed", directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0").Failure);
        }

        [Fact]
        public void SourceFiles_IncludedThroughAProperty_UseItsValueWhenItWasAssigned()
        {
            // As in MSBuild, LibDir takes the value Root has when LibDir is assigned, not Root's later value.
            var fileSystem = FileSystem("#:property LibDir=$(Root)\n#:property Root=b\n#:include $(LibDir)/shared.cs\n", new()
            {
                [RootPropsPath] = "<Project><PropertyGroup><Root>a</Root></PropertyGroup></Project>",
                [XFS.Path(@"c:\repo\a\shared.cs")] = string.Empty,
                [XFS.Path(@"c:\repo\b\shared.cs")] = string.Empty,
            });

            Assert.Equal([AppPath, XFS.Path(@"c:\repo\a\shared.cs")], Directives(fileSystem).SourceFiles(AppPath));
        }

        [Fact]
        public void SourceFiles_IncludedThroughAPropertyAssignedFromAnUndefinedOne_AreNotFollowed()
        {
            // MSBuild expands $(Root) to empty when LibDir is assigned; the later Root isn't used for the path.
            var fileSystem = FileSystem("#:property LibDir=$(Root)\n#:property Root=b\n#:include $(LibDir)/shared.cs\n#:package Foo@1.0.0\n", new()
            {
                [XFS.Path(@"c:\repo\b\shared.cs")] = "#:package Foo@1.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath], directives.SourceFiles(AppPath));
            Assert.Contains("could not be followed", directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0").Failure);
        }

        [Fact]
        public void References_WithAQuotedPropertyVersion_ResolveTheDecodedValue()
        {
            var fileSystem = FileSystem("#:property FooVersion=\"1.2.3\"\n#:package Foo@$(FooVersion)\n");

            Assert.Equal("Package Foo@1.2.3", Summary(Directives(fileSystem).References(AppPath)));
        }

        [Fact]
        public void SourceFiles_FollowALongChainOfIncludes()
        {
            const int depth = 40;
            var files = new Dictionary<string, string>();
            for (var i = 1; i <= depth; i++)
            {
                files[XFS.Path($@"c:\repo\f{i}.cs")] = i < depth ? $"#:include f{i + 1}.cs\n" : "#:package Foo@1.0.0\n";
            }

            var fileSystem = FileSystem("#:include f1.cs\n", files);
            var directives = Directives(fileSystem);

            Assert.Equal(depth + 1, directives.SourceFiles(AppPath).Count);
            Assert.Equal(XFS.Path($@"c:\repo\f{depth}.cs"), Assert.Single(directives.References(AppPath)).FilePath);
        }

        [Theory]
        [InlineData("/*\n#:include lib/shared.cs\n#:package Foo@1.0.0\n*/\nConsole.WriteLine();\n")]
        [InlineData("/* #:include lib/shared.cs */ #:package Foo@1.0.0\n")]
        [InlineData("Console.WriteLine(\"\"\"\n#:include lib/shared.cs\n#:package Foo@1.0.0\n\"\"\");\n")]
        [InlineData("#if DEBUG\n#:include lib/shared.cs\n#:package Foo@1.0.0\n#endif\n")]
        public void Directives_InCommentsOrAfterTheFirstToken_AreNotRead(string app)
        {
            var fileSystem = FileSystem(app, new() { [SharedPath] = "#:package Bar@2.0.0\n" });
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath], directives.SourceFiles(AppPath));
            Assert.Empty(directives.References(AppPath));
            Assert.False(directives.IncludesFiles(AppPath));
        }

        [Fact]
        public void Directives_AfterCommentsAndOtherPreprocessorDirectives_AreRead()
        {
            var fileSystem = FileSystem("#!/usr/bin/env dotnet\n// header\n/* licence\n   text */\n#nullable enable\n#:include lib/shared.cs\n", new()
            {
                [SharedPath] = "#:package Bar@2.0.0\n",
            });

            Assert.Equal("Package Bar@2.0.0", Summary(Directives(fileSystem).References(AppPath)));
        }

        [Fact]
        public void SourceFiles_WhosePathsDifferOnlyInCase_AreDifferentFilesOnACaseSensitiveFileSystem()
        {
            var upperPath = XFS.Path(@"c:\repo\lib\Shared.cs");
            var fileSystem = FileSystem("#:include lib/*.cs\n", new()
            {
                [SharedPath] = "#:package Foo@1.0.0\n",
                [upperPath] = "#:package Foo@1.0.0\n",
            });
            if (fileSystem.AllFiles.Count() < 3)
            {
                // The mock file system is case-insensitive here, so there is only one file.
                return;
            }

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal(2, write.ChangedFiles.Count);
            Assert.Equal("#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
            Assert.Equal("#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(upperPath));
        }

        [Fact]
        public void SourceFiles_ThatIncludeEachOther_AreEachReadOnce()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new()
            {
                [SharedPath] = "#:include nested.cs\n#:include ../app.cs\n",
                [NestedPath] = "#:include shared.cs\n#:include *.cs\n",
            });

            Assert.Equal([AppPath, SharedPath, NestedPath], Directives(fileSystem).SourceFiles(AppPath));
        }

        [Fact]
        public void WriteDirective_ForAnSdk_UpdatesTheDirectiveInEveryFileThatDeclaresIt()
        {
            const string app = "#:include lib/*.cs\n#:sdk Cake.Sdk@6.0.0\n";
            const string shared = "#:sdk cake.sdk@6.0.0 // shared\n#:package Cake.Sdk@6.0.0\n";
            const string nested = "#:sdk Cake.Sdk.Extra@6.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared, [NestedPath] = nested });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Sdk, "Cake.Sdk", "6.1.0");

            Assert.Equal([AppPath, SharedPath], write.ChangedFiles);

            Assert.Equal(app.Replace("6.0.0", "6.1.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:sdk cake.sdk@6.1.0 // shared\n#:package Cake.Sdk@6.0.0\n", fileSystem.File.ReadAllText(SharedPath));
            Assert.Equal(nested, fileSystem.File.ReadAllText(NestedPath));
        }

        [Fact]
        public void WriteDirective_WritesOnlyTheIncludedFileThatDeclaresThePackage()
        {
            // The issue this guards: the upgraded directive used to be added to the entry point.
            const string app = "#!/usr/bin/env dotnet\n#:include ./lib/shared.cs\nConsole.WriteLine(Shared.Hello());\n";
            const string shared = "#:package Foo@1.0.0 Aliases=foo\n#:package Foo.Bar@1.0.0\npublic static class Shared { }\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "foo", "2.0.0");

            Assert.Null(write.Failure);
            Assert.Equal([SharedPath], write.DeclaringFiles);
            Assert.Equal([SharedPath], write.ChangedFiles);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared.Replace("Foo@1.0.0 ", "Foo@2.0.0 "), fileSystem.File.ReadAllText(SharedPath));
        }

        [Theory]
        [InlineData("#:package Foo@1.0.0", "2.0.0", "#:package Foo@2.0.0")]
        [InlineData("#:package Foo@[1.0.0, 3.0.0)", "[2.0.0, 3.0.0)", "#:package Foo@\"[2.0.0, 3.0.0)\"")]
        [InlineData("#:package Foo@\"[1.0.0, 3.0.0)\" // pinned", "[2.0.0, 3.0.0)", "#:package Foo@\"[2.0.0, 3.0.0)\" // pinned")]
        [InlineData("#:package Foo@[1.0.0,3.0.0)", "2.0.0", "#:package Foo@2.0.0")]
        public void WriteDirective_ReplacesTheWholeVersion(string directive, string versionText, string expected)
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new() { [SharedPath] = directive + "\n" });

            Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", versionText);

            Assert.Equal(expected + "\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Theory]
        [InlineData("#:package Foo @ 1.0.0", "#:package Foo @ 2.0.0")]
        [InlineData("#:package Foo @ 1.0.0 Note = \"a b\"", "#:package Foo @ 2.0.0 Note = \"a b\"")]
        [InlineData("#:package \"Foo\"@1.0.0", "#:package \"Foo\"@2.0.0")]
        // A quoted value stays quoted: unquoted, the SDK would read the version and the metadata after it as one value.
        [InlineData("#:package Foo@\"1.0.0\" PrivateAssets=all", "#:package Foo@\"2.0.0\" PrivateAssets=all")]
        [InlineData("#:package Foo @ \"1.0.0\" PrivateAssets=all", "#:package Foo @ \"2.0.0\" PrivateAssets=all")]
        [InlineData("#:package Foo\t@1.0.0", "#:package Foo\t@2.0.0")]
        public void WriteDirective_WithWhitespaceAroundTheSeparator_RewritesTheDirective(string directive, string expected)
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new() { [SharedPath] = directive + "\n" });
            var directives = Directives(fileSystem);

            Assert.Equal("Package Foo@1.0.0", Summary(directives.References(AppPath)));

            var write = directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal([SharedPath], write.ChangedFiles);
            Assert.Equal("#:include lib/shared.cs\n", fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(expected + "\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_LeavesDirectiveTextInCommentsAlone()
        {
            const string app = "#:include lib/shared.cs\n/*\n#:package Foo@1.0.0\n*/\nConsole.WriteLine(\"#:package Foo@1.0.0\");\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = "#:package Foo@1.0.0\n" });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal([SharedPath], write.DeclaringFiles);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_DeclaredInSeveralFiles_WritesEachOne()
        {
            // The SDK rejects a package declared with different versions, so every declaration moves together.
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Foo@1.0.0\n", new() { [SharedPath] = "#:package FOO@1.0.0\n" });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal([AppPath, SharedPath], write.DeclaringFiles);
            Assert.Equal([AppPath, SharedPath], write.ChangedFiles);
            Assert.Equal("#:include lib/shared.cs\n#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:package FOO@2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_AlreadyAtTheVersion_ChangesNothing()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new() { [SharedPath] = "#:package Foo@2.0.0\n" });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Null(write.Failure);
            Assert.Equal([SharedPath], write.DeclaringFiles);
            Assert.Empty(write.ChangedFiles);
        }

        [Theory]
        [InlineData("#!/usr/bin/env dotnet\n#:include lib/shared.cs\n\nConsole.WriteLine();\n", "#!/usr/bin/env dotnet\n#:include lib/shared.cs\n#:package Foo@2.0.0\n\nConsole.WriteLine();\n")]
        [InlineData("#:include lib/shared.cs\r\n// note\r\n#:property X=1\r\nusing System;\r\n#:package Late@1.0.0\r\n", "#:include lib/shared.cs\r\n// note\r\n#:property X=1\r\n#:package Foo@2.0.0\r\nusing System;\r\n#:package Late@1.0.0\r\n")]
        [InlineData("#:include lib/shared.cs", "#:include lib/shared.cs\n#:package Foo@2.0.0\n")]
        public void WriteDirective_DeclaredNowhere_AddsADirectiveAfterTheEntryPointsDirectives(string app, string expected)
        {
            var fileSystem = FileSystem(app, new() { [SharedPath] = "#:package Bar@1.0.0\n" });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Null(write.Failure);
            Assert.Equal([AppPath], write.DeclaringFiles);
            Assert.Equal([AppPath], write.ChangedFiles);
            Assert.Equal(expected, fileSystem.File.ReadAllText(AppPath));
        }

        [Theory]
        [InlineData("#:include lib/shared.cs\n", "#:package Foo\n", "'#:package Foo' in '")]
        [InlineData("#:include $(SharedDir)/shared.cs\n", "#:package Foo@1.0.0\n", "'#:include $(SharedDir)/shared.cs'")]
        // A file that wasn't read may declare it too, even when another file does.
        [InlineData("#:include lib/shared.cs\n#:include $(OtherDir)/other.cs\n#:package Foo@1.0.0\n", "#:package Foo@1.0.0\n", "'#:include $(OtherDir)/other.cs'")]
        [InlineData("#:include lib/shared.cs\n", "#:package Foo@ ExcludeAssets=runtime\n", "has no version")]
        [InlineData("#:include lib/shared.cs\n", "#:package Foo@ ExcludeAssets = \"runtime\"\n", "has no version")]
        [InlineData("#:include lib/shared.cs\n#:property FileBasedProgramsItemMapping=$(Mapping)\n#:package Foo@1.0.0\n", "#:package Foo@1.0.0\n", "$(FileBasedProgramsItemMapping)")]
        // References skip a range that holds no single version, so only the directives show the id is a property.
        [InlineData("#:include lib/shared.cs\n#:property FooId=Foo\n", "#:package $(FooId)@[1.0.0,3.0.0)\n", "names it through a property")]
        [InlineData("#:include lib/shared.cs\n#:property FooId=Foo\n", "#:package $(FooId) @ 1.0.0\n", "names it through a property")]
        // A literal version would stop it following the property, which may be defined where this can't read it.
        [InlineData("#:include lib/shared.cs\n#:package Foo@1.0.0\n", "#:package Foo@$(FOO_VERSION)\n", "takes its version from a property")]
        // A property function isn't evaluated, so the file it names isn't known.
        [InlineData("#:include \"$([System.String]::Copy('lib/shared.cs'))\"\n#:package Foo@1.0.0\n", "#:package Foo@1.0.0\n", "could not be followed")]
        public void WriteDirective_ThatCannotBeWrittenSafely_FailsWithoutWriting(string app, string shared, string expectedFailure)
        {
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Empty(write.ChangedFiles);
            Assert.Contains(expectedFailure, write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_WhenALaterFileCannotBeWritten_ChangesBackTheFilesAlreadyWritten()
        {
            const string app = "#:include lib/shared.cs\n#:package Foo@1.0.0\n";
            const string shared = "#:package Foo@1.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            fileSystem.File.SetAttributes(SharedPath, System.IO.FileAttributes.ReadOnly);

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains($"'{SharedPath}' could not be written", write.Failure);
            Assert.Contains("No file was changed.", write.Failure);
            Assert.Empty(write.ChangedFiles);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_WhenAWriteChangesAFileAndFails_ChangesThatFileBackToo()
        {
            // An in-place write can fail after the file was truncated.
            const string app = "#:include lib/shared.cs\n#:package Foo@1.0.0\n";
            const string shared = "#:package Foo@1.0.0\n";
            var fileSystem = new PartWriteFileSystem(AppPath);
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData(shared));

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains($"'{AppPath}' could not be written", write.Failure);
            Assert.Contains("No file was changed.", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_WhenAGlobsDirectoryCannotBeRead_FailsWithoutWriting()
        {
            // The directory may hold a file that declares the package too.
            const string app = "#:include lib/**/*.cs\n#:package Foo@1.0.0\n";
            var fileSystem = new UnreadableDirectoriesFileSystem();
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData("#:package Foo@1.0.0\n"));
            var directives = Directives(fileSystem);

            Assert.Equal([AppPath], directives.SourceFiles(AppPath));
            Assert.Contains("could not be read", directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0").Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void SourceFiles_MatchAGlobWithManyWildcardsQuickly()
        {
            // A glob that backtracks on a long file name it doesn't match would take far longer than the test runs.
            var fileSystem = FileSystem("#:include lib/*a*a*a*a*a*a*a*a*a*a*a*a*b.cs\n", new()
            {
                [XFS.Path(@"c:\repo\lib\" + new string('a', 60) + ".cs")] = string.Empty,
                [XFS.Path(@"c:\repo\lib\" + new string('a', 12) + "b.cs")] = string.Empty,
            });

            Assert.Equal(["lib/" + new string('a', 12) + "b.cs"], Directives(fileSystem).SourceFiles(AppPath).Skip(1).Select(Relative));
        }

        [Theory]
        [InlineData("\r")]
        [InlineData("\u2028")]
        public void References_EndALineAtEachLineBreakCSharpHas(string lineBreak)
        {
            // A // comment ends at the line break, so the directive after it counts.
            var shared = $"// shared{lineBreak}#:package Foo@1.0.0{lineBreak}";
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Foo@1.0.0\n", new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);

            var write = directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal([AppPath, SharedPath], write.ChangedFiles);
            Assert.Equal(shared.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WriteDirective_DeclaredNowhereInAFileWithCarriageReturns_AddsALineEndedTheSameWay()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\rConsole.WriteLine();\r", new() { [SharedPath] = string.Empty });

            Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal("#:include lib/shared.cs\r#:package Foo@2.0.0\rConsole.WriteLine();\r", fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void TryUpdateVariable_WhenAnotherDeclarationUsesADifferentProperty_WarnsAndChangesNothing()
        {
            // Writing A alone would leave the declaration that reads B at the old version.
            const string app = "#:include lib/shared.cs\n#:property A=1.0.0\n#:package Foo@$(A)\n";
            const string shared = "#:property B=1.0.0\n#:package Foo@$(B)\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
            Assert.Contains(_warnings, w => w.Contains("'#:package Foo@$(B)'") && w.Contains("another property"));
        }

        [Fact]
        public void WriteDirective_WhenAnIncludedFileCannotBeRead_FailsWithoutWriting()
        {
            const string app = "#:include lib/shared.cs\n#:package Foo@1.0.0\n";
            var fileSystem = new FaultyFileSystem { Unreadable = path => path == SharedPath };
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData("#:package Foo@1.0.0\n"));

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains("is denied", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void WriteDirective_WhenAFileNowTakesItsVersionFromAProperty_FailsWithoutWriting()
        {
            // The file changed after its directives were read, so the text to rewrite is checked too.
            const string app = "#:include lib/shared.cs\n#:package Foo@1.0.0\n";
            var fileSystem = new FaultyFileSystem
            {
                ChangedContent = path => path == SharedPath ? "#:package Foo@$(V)\n" : null,
            };
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData("#:package Foo@1.0.0\n"));

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains("'#:package Foo@$(V)'", write.Failure);
            Assert.Contains("takes its version from a property", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void WriteDirective_WhenALaterFileCannotBeWritten_RestoresTheBytesOfAFileThatIsNotUtf8()
        {
            // The 0xFF in the comment can't be decoded, so only its original bytes restore the file exactly.
            byte[] app = [.. "#:include lib/shared.cs\n#:package Foo@1.0.0\n// "u8, 0xFF, .. "\n"u8];
            var fileSystem = new FaultyFileSystem();
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData("#:package Foo@1.0.0\n"));
            fileSystem.File.SetAttributes(SharedPath, System.IO.FileAttributes.ReadOnly);

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains("No file was changed.", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllBytes(AppPath));
        }

        [Fact]
        public void WriteDirective_WhenAGlobsRootCannotBeReached_FailsWithoutWriting()
        {
            // lib exists, as its parent lists it, but can't be reached, so it isn't taken to be missing.
            const string app = "#:include lib/*.cs\n#:package Foo@1.0.0\n";
            var libPath = XFS.Path(@"c:\repo\lib");
            var fileSystem = new FaultyFileSystem { Unreachable = path => path.TrimEnd('/', '\\') == libPath };
            fileSystem.AddFile(AppPath, new MockFileData(app));
            fileSystem.AddFile(SharedPath, new MockFileData("#:package Foo@1.0.0\n"));

            var write = Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Contains("could not be read", write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void WriteDirective_DeclaredNowhereInAFileWithUnicodeLineSeparators_AddsALineEndedTheSameWay()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\u2028Console.WriteLine();\u2028", new() { [SharedPath] = string.Empty });

            Directives(fileSystem).WriteDirective(AppPath, ReferenceKind.Package, "Foo", "2.0.0");

            Assert.Equal("#:include lib/shared.cs\u2028#:package Foo@2.0.0\u2028Console.WriteLine();\u2028", fileSystem.File.ReadAllText(AppPath));
        }

        [Fact]
        public void References_ThroughAnImportedAliasOfAPropertyTheAppRedefines_AreNotResolved()
        {
            // Frozen took V's imported value when Directory.Build.props assigned it, before the app's V, so neither
            // the app's V nor the alias says which definition holds the version.
            const string app = "#:include lib/shared.cs\n#:property V=2.0.0\n";
            var fileSystem = FileSystem(app, new()
            {
                [RootPropsPath] = "<Project><PropertyGroup><V>1.0.0</V><Frozen>$(V)</Frozen></PropertyGroup></Project>",
                [SharedPath] = "#:package Foo@$(Frozen)\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal("", Summary(directives.References(AppPath)));
            Assert.Empty(directives.PackageVariables(AppPath, ReferenceKind.Package));
            Assert.Contains("takes its version from a property", directives.WriteDirective(AppPath, ReferenceKind.Package, "Foo", "3.0.0").Failure);
        }

        [Fact]
        public void TryUpdateVariable_UsedByAnIncludePath_WarnsAndChangesNothing()
        {
            // A new value would include another file, which may define the property or declare the package too.
            const string app = "#:property V=1.0.0\n#:property LibDir=lib/$(V)\n#:include $(LibDir)/shared.cs\n#:package Foo@$(V)\n";
            var included = XFS.Path(@"c:\repo\lib\1.0.0\shared.cs");
            var fileSystem = FileSystem(app, new() { [included] = "#:property V=1.0.0\n" });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:property V=1.0.0\n", fileSystem.File.ReadAllText(included));
            Assert.Contains(_warnings, w => w.Contains("'#:include $(LibDir)/shared.cs'") && w.Contains("uses $(V)"));
        }

        [Fact]
        public void TryUpdateVariable_DefinedInAnIncludedFile_UpdatesThePropertyWhereItIsDefined()
        {
            const string app = "#:include lib/shared.cs\n#:package Foo@$(FooVersion)\n";
            const string shared = "#:property FooVersion=1.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:property FooVersion=2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void TryUpdateVariable_KeepsTheDirectivesMetadataAndComments()
        {
            const string app = "#:include lib/shared.cs\n#:property FooVersion=1.0.0\n";
            const string shared = "#:package Foo@$(FooVersion) Aliases=foo // aliased\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void TryUpdateVariable_ForAnSdkNamedThroughAProperty_KeepsEachDirectivesNameAndComments()
        {
            // Other apps that include the shared file may not define $(SdkId), so its literal name is kept.
            const string app = "#:include lib/shared.cs\n#:property SdkId=Cake.Sdk\n#:sdk $(SdkId)@6.0.0\n";
            const string shared = "#:sdk cake.sdk @ 6.0.0 // pinned\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Sdk)["Cake.Sdk"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("6.1.0")));

            Assert.Equal(app.Replace("6.0.0", "6.1.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:sdk cake.sdk @ 6.1.0 // pinned\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void TryUpdateVariable_KeepsALiteralVersionInAnotherFileLiteral()
        {
            // Other apps that include the shared file may not define $(FooVersion), so it keeps a literal version.
            const string app = "#:include lib/shared.cs\n#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            const string shared = "#:package Foo@1.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void TryUpdateVariable_DefinedInAnIncludedFileOfAnotherExtension_UpdatesItThere()
        {
            // The item mapping makes .txt files Compile items, so their directives count, as for the SDK.
            const string app = "#:property FileBasedProgramsItemMapping=.cs=Compile;.txt=Compile\n#:include lib/versions.txt\n#:package Foo@$(FooVersion)\n";
            var versionsPath = XFS.Path(@"c:\repo\lib\versions.txt");
            var fileSystem = FileSystem(app, new() { [versionsPath] = "#:property FooVersion=1.0.0\n" });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.Equal([versionsPath], directives.VariableFiles(AppPath, variableInfo));
            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:property FooVersion=2.0.0\n", fileSystem.File.ReadAllText(versionsPath));
            Assert.Empty(_warnings);
        }

        [Theory]
        [InlineData("#:package \"Foo\"@1.0.0", "Package Foo@1.0.0")]
        [InlineData("#:package \"F\\x6fo\" @ \"1.0.0\"", "Package Foo@1.0.0")]
        [InlineData("#:sdk Cake.Sdk @ 6.0.0", "Sdk Cake.Sdk@6.0.0")]
        [InlineData("#:property FooVersion = \"1.0.0\"\n#:package Foo@$(FooVersion)", "Package Foo@1.0.0")]
        // Not directives the SDK accepts.
        [InlineData("#:package \"Foo@1.0.0", "")]
        [InlineData("#:package Foo@1.0.0 \"x\"", "")]
        [InlineData("#:sdk Cake.Sdk@6.0.0 Extra=1", "")]
        [InlineData("#:Package Foo@1.0.0", "")]
        public void References_AreParsedAsTheSdkParsesDirectives(string app, string expected)
        {
            var fileSystem = FileSystem(app + "\n");

            Assert.Equal(expected, Summary(Directives(fileSystem).References(AppPath)));
        }

        [Fact]
        public void DeclaredPackages_IncludeThoseWithoutAVersionReferencesCanRead()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Bar@1.0.0\n", new()
            {
                [SharedPath] = "#:package Foo@[1.0.0,3.0.0)\n#:package Baz\n#:sdk Cake.Sdk@6.0.0\n",
            });
            var directives = Directives(fileSystem);

            Assert.Equal("Package Bar@1.0.0, Sdk Cake.Sdk@6.0.0", Summary(directives.References(AppPath)));
            Assert.Equal(["Bar", "Baz", "Foo"], directives.DeclaredPackages(AppPath).Order());
        }

        [Fact]
        public void TryUpdateVariable_DefinedInSeveralFiles_UpdatesEachDefinition()
        {
            // The SDK rejects the same property declared with different values, so every declaration moves together.
            const string app = "#:include lib/shared.cs\n#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            const string shared = "#:property fooversion = \"1.0.0\"\n#:property FooVersionSuffix=1.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.Equal([AppPath, SharedPath], directives.VariableFiles(AppPath, variableInfo));
            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app.Replace("1.0.0", "2.0.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:property fooversion = \"2.0.0\"\n#:property FooVersionSuffix=1.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void TryUpdateVariable_WhenAnIncludeCannotBeFollowed_WarnsAndChangesNothing()
        {
            const string app = "#:include $(OtherDir)/other.cs\n#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            var fileSystem = FileSystem(app);
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Package)["Foo"];

            Assert.False(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Contains(_warnings, w => w.Contains("'#:include $(OtherDir)/other.cs'"));
        }

        [Fact]
        public void TryUpdateVariable_ForAnSdkNamedThroughAProperty_UpdatesItsDirectiveInEveryFile()
        {
            const string app = "#:include lib/shared.cs\n#:property SdkId=Cake.Sdk\n#:sdk $(SdkId)@6.0.0\n";
            const string shared = "#:sdk $(SdkId)@6.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });
            var directives = Directives(fileSystem);
            var variableInfo = directives.PackageVariables(AppPath, ReferenceKind.Sdk)["Cake.Sdk"];

            Assert.True(directives.TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("6.1.0")));

            Assert.Equal(app.Replace("6.0.0", "6.1.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared.Replace("6.0.0", "6.1.0"), fileSystem.File.ReadAllText(SharedPath));
        }

        // A file system on which the first write to one file empties it and then fails, as an in-place write can.
        private sealed class PartWriteFileSystem : MockFileSystem
        {
            private readonly PartWriteFile _file;

            public PartWriteFileSystem(string failingPath) => _file = new PartWriteFile(this, failingPath);

            public override IFile File => _file;

            private sealed class PartWriteFile(IMockFileDataAccessor files, string failingPath) : MockFile(files)
            {
                private bool _failed;

                public override void WriteAllText(string path, string? contents)
                {
                    FailOnce(path);
                    base.WriteAllText(path, contents);
                }

                public override void WriteAllBytes(string path, byte[] bytes)
                {
                    FailOnce(path);
                    base.WriteAllBytes(path, bytes);
                }

                private void FailOnce(string path)
                {
                    if (!_failed && path == failingPath)
                    {
                        _failed = true;
                        base.WriteAllText(path, string.Empty);
                        throw new System.IO.IOException("The disk is full.");
                    }
                }
            }
        }

        // A file system with faults: files that can't be read, files whose content changes once read as text (as if
        // edited since), and directories that can't be reached though their parent lists them. Text that isn't valid
        // UTF-8 is read as File.ReadAllText reads it, with replacement characters.
        private sealed class FaultyFileSystem : MockFileSystem
        {
            private FaultyFile? _file;
            private FaultyDirectory? _directory;

            public Func<string, bool> Unreadable { get; init; } = _ => false;

            public Func<string, string?> ChangedContent { get; init; } = _ => null;

            public Func<string, bool> Unreachable { get; init; } = _ => false;

            public override IFile File => _file ??= new FaultyFile(this);

            public override IDirectory Directory => _directory ??= new FaultyDirectory(this, base.Directory.GetCurrentDirectory());

            private sealed class FaultyFile(FaultyFileSystem fileSystem) : MockFile(fileSystem)
            {
                public override string ReadAllText(string path)
                {
                    Check(path);
                    return System.Text.Encoding.UTF8.GetString(base.ReadAllBytes(path));
                }

                public override byte[] ReadAllBytes(string path)
                {
                    Check(path);
                    return fileSystem.ChangedContent(path) is { } changed ? System.Text.Encoding.UTF8.GetBytes(changed) : base.ReadAllBytes(path);
                }

                private void Check(string path)
                {
                    if (fileSystem.Unreadable(path))
                    {
                        throw new UnauthorizedAccessException($"Access to '{path}' is denied.");
                    }
                }
            }

            private sealed class FaultyDirectory(FaultyFileSystem fileSystem, string currentDirectory) : MockDirectory(fileSystem, currentDirectory)
            {
                public override bool Exists(string? path) => path != null && !fileSystem.Unreachable(path) && base.Exists(path);
            }
        }

        // A file system whose directories can't be searched.
        private sealed class UnreadableDirectoriesFileSystem : MockFileSystem
        {
            private UnreadableDirectory? _directory;

            public override IDirectory Directory => _directory ??= new UnreadableDirectory(this, base.Directory.GetCurrentDirectory());

            private sealed class UnreadableDirectory(IMockFileDataAccessor files, string currentDirectory) : MockDirectory(files, currentDirectory)
            {
                public override IEnumerable<string> EnumerateFiles(string path, string searchPattern, System.IO.SearchOption searchOption) =>
                    throw new UnauthorizedAccessException($"Access to '{path}' is denied.");
            }
        }
    }
}
