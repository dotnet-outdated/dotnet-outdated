#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    /// <summary>
    /// Upgrades work from what an <see cref="IProjectEvaluator"/> reports, so another evaluator (such as one that loads
    /// MSBuild in-process) can take the place of <see cref="DotNetProjectEvaluator"/>.
    /// </summary>
    public class ProjectEvaluatorSeamTests
    {
        private sealed class FakeProjectEvaluator : IProjectEvaluator
        {
            public Dictionary<string, PackageVersionDeclaration> PackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

            public Task<DependencyGraphSpec> GetRestoreGraphAsync(string projectPath, string? runtime) =>
                throw new NotSupportedException();

            public IReadOnlyList<PackageReferenceItem>? GetPackageReferenceItems(string projectPath, string targetFramework, string? runtime) =>
                throw new NotSupportedException();

            public IReadOnlyDictionary<string, PackageVersionDeclaration> GetPackageVersions(string projectPath, string? targetFramework = null) => PackageVersions;


            public IReadOnlyDictionary<string, ImportedProperty> GetImportedProperties(string appPath) =>
                new Dictionary<string, ImportedProperty>();
        }

        [Fact]
        public void AddPackage_WritesTheVersionVariableWhereTheEvaluatorSaysItIsDefined()
        {
            // eng/Versions.props isn't imported by any file the static evaluator reads (an SDK such as Arcade imports
            // it), so only the evaluator can say that $(FooVersion) is defined there.
            var projectPath = XFS.Path(@"c:\repo\src\App\App.csproj");
            var versionsPath = XFS.Path(@"c:\repo\eng\Versions.props");
            const string project = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Foo" Version="$(FooVersion)" />
                  </ItemGroup>
                </Project>
                """;
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(project),
                [versionsPath] = new("""
                    <Project>
                      <PropertyGroup>
                        <FooVersion>1.0.0</FooVersion>
                      </PropertyGroup>
                    </Project>
                    """),
            });

            var evaluator = new FakeProjectEvaluator();
            var variable = new PackageVariableInfo
            {
                PackageName = "Foo",
                VariableName = "FooVersion",
                VariableValue = "1.0.0",
                FilePath = versionsPath,
                PropertyElementIndex = 0,
                PackageReferenceFilePath = projectPath,
                PackageReferenceVersion = "$(FooVersion)",
                ElementType = "PackageReference",
            };
            evaluator.PackageVersions["Foo"] = new PackageVersionDeclaration(
                "PackageReference", projectPath, "$(FooVersion)", IsVersionOverride: false, VersionRange.Parse("1.0.0"), variable);

            var runner = Substitute.For<IDotNetRunner>();
            var service = new ReferenceUpgrader(runner, fileSystem, evaluator);

            var status = service.Upgrade(projectPath, "Foo", "net9.0", new NuGetVersion("2.0.0"), noRestore: true);

            Assert.True(status.IsSuccess, status.FailureDetails);
            Assert.Contains("<FooVersion>2.0.0</FooVersion>", fileSystem.File.ReadAllText(versionsPath), StringComparison.Ordinal);
            Assert.Equal(project, fileSystem.File.ReadAllText(projectPath));
            runner.DidNotReceiveWithAnyArgs().Run(default, default!);
        }
    }
}
