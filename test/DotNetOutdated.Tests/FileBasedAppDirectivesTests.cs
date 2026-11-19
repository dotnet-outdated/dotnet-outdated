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
        [InlineData("#:package Foo@ 1.0.0 PrivateAssets=all", "#:package Foo@ 2.0.0 PrivateAssets=all")]
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
        [InlineData("#:include lib/shared.cs\n", "#:package Foo@ ExcludeAssets=runtime\n", "has no version")]
        // References skip a range that holds no single version, so only the directives show the id is a property.
        [InlineData("#:include lib/shared.cs\n#:property FooId=Foo\n", "#:package $(FooId)@[1.0.0,3.0.0)\n", "names it through a property")]
        [InlineData("#:include lib/shared.cs\n#:property FooId=Foo\n", "#:package $(FooId) @ 1.0.0\n", "names it through a property")]
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
