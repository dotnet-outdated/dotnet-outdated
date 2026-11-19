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
            Assert.False(directives.TryUpdateDirectReference(filePath, "Foo", ReferenceKind.Package, new NuGetVersion("2.0.0")));
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
        public void TryUpdateVariable_WhenTheUpdateThrowsAfterRewritingTheProperty_WarnsAndReportsTheUpdate()
        {
            const string app = "#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            var fileSystem = FileSystem(app);
            var variableInfo = Directives(fileSystem).PackageVariables(AppPath, ReferenceKind.Package)["Foo"];
            var evaluator = Substitute.For<IProjectEvaluator>();
            evaluator.GetImportedProperties(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("boom"));

            Assert.True(Directives(fileSystem, evaluator).TryUpdateVariable(AppPath, variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(app.Replace("FooVersion=1.0.0", "FooVersion=2.0.0"), fileSystem.File.ReadAllText(AppPath));
            var warning = Assert.Single(_warnings);
            Assert.Contains("'Foo'", warning);
            Assert.Contains("boom", warning);
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
        [InlineData("#:include lib/**", "lib/a.cs, lib/nested.cs, lib/shared.cs, lib/sub/b.cs")]
        [InlineData("#:include **/*.cs\n#:exclude lib/shared.cs\n#:exclude lib/sub/*.cs", "lib/a.cs, lib/nested.cs, my lib/c.cs, other.cs")]
        [InlineData("#:include lib/a.cs\n#:include lib/a.cs\n#:include missing/*.cs\n#:include lib/notes.txt", "lib/a.cs")]
        [InlineData("#:include \"my lib/c.cs\" CopyToOutputDirectory=PreserveNewest", "my lib/c.cs")]
        [InlineData("#:property LibDir=lib\n#:include $(LibDir)/a.cs", "lib/a.cs")]
        [InlineData("#:include $(Undefined)/a.cs", "")]
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

        [Fact]
        public void SourceFiles_ExcludedByAnIncludedFile_AreNotCompiled_ButTheEntryPointAlwaysIs()
        {
            var fileSystem = FileSystem("#:include lib/*.cs\n", new()
            {
                [SharedPath] = "#:exclude nested.cs\n#:exclude ../app.cs\n",
                [NestedPath] = "#:package Bar@2.0.0\n",
            });

            Assert.Equal([AppPath, SharedPath], Directives(fileSystem).SourceFiles(AppPath));
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
        public void TryUpdateDirectReference_UpdatesTheDirectiveInEveryFileThatDeclaresIt()
        {
            const string app = "#:include lib/*.cs\n#:sdk Cake.Sdk@6.0.0\n";
            const string shared = "#:sdk cake.sdk@6.0.0 // shared\n#:package Cake.Sdk@6.0.0\n";
            const string nested = "#:sdk Cake.Sdk.Extra@6.0.0\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared, [NestedPath] = nested });

            Assert.True(Directives(fileSystem).TryUpdateDirectReference(AppPath, "Cake.Sdk", ReferenceKind.Sdk, new NuGetVersion("6.1.0")));

            Assert.Equal(app.Replace("6.0.0", "6.1.0"), fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:sdk cake.sdk@6.1.0 // shared\n#:package Cake.Sdk@6.0.0\n", fileSystem.File.ReadAllText(SharedPath));
            Assert.Equal(nested, fileSystem.File.ReadAllText(NestedPath));
        }

        [Fact]
        public void WritePackageDirective_WritesOnlyTheIncludedFileThatDeclaresThePackage()
        {
            // The issue this guards: the upgraded directive used to be added to the entry point.
            const string app = "#!/usr/bin/env dotnet\n#:include ./lib/shared.cs\nConsole.WriteLine(Shared.Hello());\n";
            const string shared = "#:package Foo@1.0.0 Aliases=foo\n#:package Foo.Bar@1.0.0\npublic static class Shared { }\n";
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });

            var write = Directives(fileSystem).WritePackageDirective(AppPath, "foo", "2.0.0");

            Assert.Equal(new PackageDirectiveWrite(true, SharedPath, null), write);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared.Replace("Foo@1.0.0 ", "Foo@2.0.0 "), fileSystem.File.ReadAllText(SharedPath));
        }

        [Theory]
        [InlineData("#:package Foo@1.0.0", "2.0.0", "#:package Foo@2.0.0")]
        [InlineData("#:package Foo@[1.0.0, 3.0.0)", "[2.0.0, 3.0.0)", "#:package Foo@\"[2.0.0, 3.0.0)\"")]
        [InlineData("#:package Foo@\"[1.0.0, 3.0.0)\" // pinned", "[2.0.0, 3.0.0)", "#:package Foo@\"[2.0.0, 3.0.0)\" // pinned")]
        [InlineData("#:package Foo@[1.0.0,3.0.0)", "2.0.0", "#:package Foo@2.0.0")]
        public void WritePackageDirective_ReplacesTheWholeVersion(string directive, string versionText, string expected)
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new() { [SharedPath] = directive + "\n" });

            Directives(fileSystem).WritePackageDirective(AppPath, "Foo", versionText);

            Assert.Equal(expected + "\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WritePackageDirective_DeclaredInSeveralFiles_WritesEachOne()
        {
            // The SDK rejects a package declared with different versions, so every declaration moves together.
            var fileSystem = FileSystem("#:include lib/shared.cs\n#:package Foo@1.0.0\n", new() { [SharedPath] = "#:package FOO@1.0.0\n" });

            var write = Directives(fileSystem).WritePackageDirective(AppPath, "Foo", "2.0.0");

            Assert.Equal(new PackageDirectiveWrite(true, AppPath, null), write);
            Assert.Equal("#:include lib/shared.cs\n#:package Foo@2.0.0\n", fileSystem.File.ReadAllText(AppPath));
            Assert.Equal("#:package FOO@2.0.0\n", fileSystem.File.ReadAllText(SharedPath));
        }

        [Fact]
        public void WritePackageDirective_AlreadyAtTheVersion_ChangesNothing()
        {
            var fileSystem = FileSystem("#:include lib/shared.cs\n", new() { [SharedPath] = "#:package Foo@2.0.0\n" });

            var write = Directives(fileSystem).WritePackageDirective(AppPath, "Foo", "2.0.0");

            Assert.Equal(new PackageDirectiveWrite(false, SharedPath, null), write);
        }

        [Theory]
        [InlineData("#!/usr/bin/env dotnet\n#:include lib/shared.cs\n\nConsole.WriteLine();\n", "#!/usr/bin/env dotnet\n#:include lib/shared.cs\n#:package Foo@2.0.0\n\nConsole.WriteLine();\n")]
        [InlineData("#:include lib/shared.cs\r\n// note\r\n#:property X=1\r\nusing System;\r\n#:package Late@1.0.0\r\n", "#:include lib/shared.cs\r\n// note\r\n#:property X=1\r\n#:package Foo@2.0.0\r\nusing System;\r\n#:package Late@1.0.0\r\n")]
        [InlineData("#:include lib/shared.cs", "#:include lib/shared.cs\n#:package Foo@2.0.0\n")]
        public void WritePackageDirective_DeclaredNowhere_AddsADirectiveAfterTheEntryPointsDirectives(string app, string expected)
        {
            var fileSystem = FileSystem(app, new() { [SharedPath] = "#:package Bar@1.0.0\n" });

            var write = Directives(fileSystem).WritePackageDirective(AppPath, "Foo", "2.0.0");

            Assert.Equal(new PackageDirectiveWrite(true, AppPath, null), write);
            Assert.Equal(expected, fileSystem.File.ReadAllText(AppPath));
        }

        [Theory]
        [InlineData("#:include lib/shared.cs\n", "#:package Foo\n", "'#:package Foo' in '")]
        [InlineData("#:include $(SharedDir)/shared.cs\n", "#:package Foo@1.0.0\n", "'#:include $(SharedDir)/shared.cs'")]
        public void WritePackageDirective_ThatCannotBeWrittenSafely_FailsWithoutWriting(string app, string shared, string expectedFailure)
        {
            var fileSystem = FileSystem(app, new() { [SharedPath] = shared });

            var write = Directives(fileSystem).WritePackageDirective(AppPath, "Foo", "2.0.0");

            Assert.False(write.Changed);
            Assert.Contains(expectedFailure, write.Failure);
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Equal(shared, fileSystem.File.ReadAllText(SharedPath));
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
    }
}
