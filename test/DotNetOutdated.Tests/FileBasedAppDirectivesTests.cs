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

            Assert.True(directives.TryUpdateVariable(variableInfo, new NuGetVersion("2.0.0")));

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

            Assert.True(directives.TryUpdateVariable(variableInfo, new NuGetVersion("2.0.0")));

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

            Assert.False(directives.TryUpdateVariable(variableInfo, new NuGetVersion("1.0.0")));

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

            Assert.True(directives.TryUpdateVariable(variableInfo, new NuGetVersion("2.0.0")));

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

            Assert.False(directives.TryUpdateVariable(variableInfo, new NuGetVersion("2.0.0")));

            Assert.Equal(props, fileSystem.File.ReadAllText(RootPropsPath));
            Assert.Equal(app, fileSystem.File.ReadAllText(AppPath));
            Assert.Contains(_warnings, w => w.Contains("$(FooVersion)") && w.Contains("could not be determined"));
        }

        [Fact]
        public void TryUpdateVariable_WhenTheUpdateThrows_WarnsAndReturnsFalse()
        {
            const string app = "#:property FooVersion=1.0.0\n#:package Foo@$(FooVersion)\n";
            var fileSystem = FileSystem(app);
            var variableInfo = Directives(fileSystem).PackageVariables(AppPath, ReferenceKind.Package)["Foo"];
            var evaluator = Substitute.For<IProjectEvaluator>();
            evaluator.GetImportedProperties(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("boom"));

            Assert.False(Directives(fileSystem, evaluator).TryUpdateVariable(variableInfo, new NuGetVersion("2.0.0")));

            var warning = Assert.Single(_warnings);
            Assert.Contains("'Foo'", warning);
            Assert.Contains("boom", warning);
        }
    }
}
