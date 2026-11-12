#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class ProjectAnalysisServiceTests
    {
        [Fact]
        public async Task ACancelledRequest_DoesNotEvaluateTheProject()
        {
            var evaluator = Substitute.For<IProjectEvaluator>();
            var service = new ProjectAnalysisService(evaluator, Substitute.For<IDotNetRestoreService>(), new MockFileSystem(), NullLogger.Instance);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.AnalyzeProjectAsync("/repo/App.csproj", cancellationToken: cancellation.Token));

            await evaluator.DidNotReceiveWithAnyArgs().GetRestoreGraphAsync(default!, default);
        }

        [Fact]
        public async Task APackageAndAnSdkWithTheSameId_AreSeparateDependencies()
        {
            var framework = NuGetFramework.Parse("net9.0");
            var directory = Path.Combine(Path.GetTempPath(), "dotnet-outdated-same-id-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var projectPath = Path.Combine(directory, "App.csproj");
                await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Cake.Sdk/5.0.0\" />");

                var packageSpec = new PackageSpec(
                [
                    new TargetFrameworkInformation
                    {
                        FrameworkName = framework,
                        TargetAlias = "net9.0",
                        Dependencies =
                        [
                            new LibraryDependency
                            {
                                LibraryRange = new LibraryRange("Cake.Sdk", VersionRange.Parse("4.0.0"), LibraryDependencyTarget.Package),
                            },
                        ],
                    },
                ])
                {
                    Name = "App",
                    FilePath = projectPath,
                    RestoreMetadata = new ProjectRestoreMetadata
                    {
                        ProjectStyle = ProjectStyle.PackageReference,
                        ProjectPath = projectPath,
                        ProjectUniqueName = projectPath,
                        OutputPath = directory,
                        PackagesPath = Path.Combine(directory, "packages"),
                    },
                };
                var graph = new DependencyGraphSpec();
                graph.AddProject(packageSpec);

                var target = new LockFileTarget { TargetFramework = framework };
                target.Libraries.Add(new LockFileTargetLibrary { Name = "Cake.Sdk", Version = new NuGetVersion("4.0.0"), Type = "package" });
                var lockFile = new LockFile { Version = LockFileFormat.LegacyVersion };
                lockFile.Targets.Add(target);
                new LockFileFormat().Write(Path.Combine(directory, "project.assets.json"), lockFile);

                var evaluator = Substitute.For<IProjectEvaluator>();
                evaluator.GetRestoreGraphAsync(projectPath, Arg.Any<string?>()).Returns(graph);
                var service = new ProjectAnalysisService(evaluator, Substitute.For<IDotNetRestoreService>(), new FileSystem(), NullLogger.Instance);

                var project = Assert.Single(await service.AnalyzeProjectAsync(projectPath));

                var dependencies = Assert.Single(project.TargetFrameworks).Dependencies;
                Assert.Equal(2, dependencies.Count);
                Assert.Equal(new NuGetVersion("4.0.0"), dependencies.Single(d => d.Name == "Cake.Sdk" && d.Kind == ReferenceKind.Package).ResolvedVersion);
                Assert.Equal(new NuGetVersion("5.0.0"), dependencies.Single(d => d.Name == "Cake.Sdk" && d.Kind == ReferenceKind.Sdk).ResolvedVersion);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Theory]
        [InlineData(1, new[] { "CHILD" })]
        [InlineData(2, new[] { "CHILD", "Grandchild" })]
        [InlineData(3, new[] { "CHILD", "Grandchild", "GreatGrandchild" })]
        public async Task TransitiveDependencies_AreAddedDownToTheTransitiveDepth(int depth, string[] expectedTransitive)
        {
            using var project = new TemporaryProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var target = project.Target;
            target.Libraries.Add(Library("Parent", "1.0.0", ("CHILD", "[2.0.0, )")));
            target.Libraries.Add(Library("Child", "2.1.0", ("Grandchild", "3.0.0")));
            target.Libraries.Add(Library("Grandchild", "3.0.0", ("GreatGrandchild", "4.0.0")));
            target.Libraries.Add(Library("GreatGrandchild", "4.0.0"));
            project.Write(directDependencies: ["Parent"]);

            var analyzed = await project.AnalyzeAsync(new ProjectAnalysisOptions { IncludeTransitiveDependencies = true, TransitiveDepth = depth });

            Assert.Equal("App", analyzed.Name);
            var dependencies = Assert.Single(analyzed.TargetFrameworks).Dependencies;
            var parent = Assert.Single(dependencies, d => !d.IsTransitive);
            Assert.Equal("Parent", parent.Name);
            // Without the package in the packages folder it can't be a development dependency.
            Assert.False(parent.IsDevelopmentDependency);
            Assert.Equal(expectedTransitive, dependencies.Where(d => d.IsTransitive).Select(d => d.Name).Order(StringComparer.Ordinal));

            // The child library is matched to the dependency ignoring case.
            var child = dependencies.Single(d => d.Name == "CHILD");
            Assert.Equal(VersionRange.Parse("[2.0.0, )"), child.VersionRange);
            Assert.Equal(new NuGetVersion("2.1.0"), child.ResolvedVersion);
        }

        [Fact]
        public async Task ASolution_ReportsEachProjectWithItsOwnNameAndPath()
        {
            using var project = new TemporaryProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
            project.Target.Libraries.Add(Library("Parent", "1.0.0"));
            project.Write(directDependencies: ["Parent"]);

            var analyzed = await project.AnalyzeAsync(ProjectAnalysisOptions.Default, Path.Combine(project.Directory, "App.sln"));

            Assert.Equal("App", analyzed.Name);
            Assert.Equal(project.ProjectPath, analyzed.FilePath);
        }

        [Fact]
        public async Task TransitiveDependencies_AreLeftOutUnlessRequested()
        {
            using var project = new TemporaryProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
            project.Target.Libraries.Add(Library("Parent", "1.0.0", ("Child", "2.0.0")));
            project.Target.Libraries.Add(Library("Child", "2.0.0"));
            project.Write(directDependencies: ["Parent"]);

            var analyzed = await project.AnalyzeAsync(new ProjectAnalysisOptions { TransitiveDepth = 5 });

            Assert.Equal(["Parent"], Assert.Single(analyzed.TargetFrameworks).Dependencies.Select(d => d.Name));
        }

        [Fact]
        public async Task TransitiveDependencies_DoNotReplaceADirectDependencyOrRepeatAPackage()
        {
            using var project = new TemporaryProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var target = project.Target;
            target.Libraries.Add(Library("First", "1.0.0", ("Shared", "1.0.0"), ("Second", "1.0.0")));
            target.Libraries.Add(Library("Second", "1.0.0", ("Shared", "2.0.0")));
            target.Libraries.Add(Library("Shared", "2.0.0", ("Leaf", "1.0.0")));
            target.Libraries.Add(Library("Leaf", "1.0.0"));
            // "Missing" is a direct dependency without a library in the lock file, such as one that failed to restore.
            project.Write(directDependencies: ["Second", "First", "Missing"]);

            var analyzed = await project.AnalyzeAsync(new ProjectAnalysisOptions { IncludeTransitiveDependencies = true, TransitiveDepth = 5 });

            var dependencies = Assert.Single(analyzed.TargetFrameworks).Dependencies;
            Assert.Equal(["First", "Leaf", "Missing", "Second", "Shared"], dependencies.Select(d => d.Name).Order(StringComparer.Ordinal));
            Assert.Equal(["First", "Missing", "Second"], dependencies.Where(d => !d.IsTransitive).Select(d => d.Name).Order(StringComparer.Ordinal));
            // The first path to a package decides its range, and it is not added again from a later one.
            Assert.Equal(VersionRange.Parse("2.0.0"), dependencies.Single(d => d.Name == "Shared").VersionRange);
            Assert.Null(dependencies.Single(d => d.Name == "Missing").ResolvedVersion);
        }

        [Fact]
        public async Task TransitiveDependencies_DoNotTurnALaterDirectDependencyIntoATransitiveOne()
        {
            // For example Microsoft.Extensions.Hosting (direct) depends on Microsoft.Extensions.Logging (also direct).
            using var project = new TemporaryProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
            project.Target.Libraries.Add(Library("First", "1.0.0", ("Second", "1.0.0")));
            project.Target.Libraries.Add(Library("Second", "1.0.0"));
            project.Write(directDependencies: ["First", "Second"]);

            var analyzed = await project.AnalyzeAsync(new ProjectAnalysisOptions { IncludeTransitiveDependencies = true });

            var second = Assert.Single(analyzed.TargetFrameworks).Dependencies.Single(d => d.Name == "Second");
            Assert.False(second.IsTransitive);
        }

        [Fact]
        public async Task ACustomSdkProjectThatCannotBeEvaluated_KeepsItsDependenciesAndLogsWhy()
        {
            using var project = new TemporaryProject("<Project Sdk=\"Contoso.Sdk/1.0.0\" />");
            project.Target.Libraries.Add(Library("Injected", "1.0.0"));
            project.Write(directDependencies: ["Injected"]);
            project.Evaluator.GetPackageReferenceItems(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns((IReadOnlyList<PackageReferenceItem>?)null);

            var analyzed = await project.AnalyzeAsync(ProjectAnalysisOptions.Default);

            var dependency = Assert.Single(Assert.Single(analyzed.TargetFrameworks).Dependencies, d => d.Kind == ReferenceKind.Package);
            Assert.Equal("Injected", dependency.Name);
            Assert.False(dependency.IsAutoReferenced);
            project.Logger.Received(1).LogVerbose(
                $"Could not evaluate the package references of '{project.ProjectPath}' for net9.0. Packages that its SDK adds are reported as direct dependencies.");
        }

        [Fact]
        public async Task AMalformedNuGetConfig_DoesNotStopTheAnalysisOfACustomSdkProject()
        {
            using var project = new TemporaryProject("<Project Sdk=\"Contoso.Sdk/1.0.0\" />");
            File.WriteAllText(Path.Combine(project.Directory, "NuGet.Config"), "<configuration><packageSources>");
            project.Target.Libraries.Add(Library("Injected", "1.0.0"));
            project.Write(directDependencies: ["Injected"]);
            project.Evaluator.GetPackageReferenceItems(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns([]);

            var analyzed = await project.AnalyzeAsync(ProjectAnalysisOptions.Default);

            Assert.Contains(Assert.Single(analyzed.TargetFrameworks).Dependencies, d => d.Name == "Injected");
            project.Logger.Received(1).LogVerbose(Arg.Is<string>(message =>
                message.StartsWith($"Could not read the NuGet global packages folder for '{project.ProjectPath}': ", StringComparison.Ordinal)));
        }

        private static LockFileTargetLibrary Library(string name, string version, params (string Id, string Range)[] dependencies)
        {
            var library = new LockFileTargetLibrary { Name = name, Version = new NuGetVersion(version), Type = "package" };
            foreach (var (id, range) in dependencies)
            {
                library.Dependencies.Add(new NuGet.Packaging.Core.PackageDependency(id, VersionRange.Parse(range)));
            }

            return library;
        }

        /// <summary>A project file and its lock file in a temporary directory, analyzed with a substitute evaluator.</summary>
        private sealed class TemporaryProject : IDisposable
        {
            private static readonly NuGetFramework Framework = NuGetFramework.Parse("net9.0");

            public TemporaryProject(string projectContent)
            {
                Directory = Path.Combine(Path.GetTempPath(), "dotnet-outdated-analysis-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(Directory);
                ProjectPath = Path.Combine(Directory, "App.csproj");
                File.WriteAllText(ProjectPath, projectContent);
            }

            public string Directory { get; }

            public string ProjectPath { get; }

            public LockFileTarget Target { get; } = new() { TargetFramework = Framework };

            public IProjectEvaluator Evaluator { get; } = Substitute.For<IProjectEvaluator>();

            public ILogger Logger { get; } = Substitute.For<ILogger>();

            public void Write(string[] directDependencies)
            {
                var packageSpec = new PackageSpec(
                [
                    new TargetFrameworkInformation
                    {
                        FrameworkName = Framework,
                        TargetAlias = "net9.0",
                        Dependencies =
                        [
                            .. directDependencies.Select(name => new LibraryDependency
                            {
                                LibraryRange = new LibraryRange(name, VersionRange.Parse("1.0.0"), LibraryDependencyTarget.Package),
                            }),
                        ],
                    },
                ])
                {
                    Name = "App",
                    FilePath = ProjectPath,
                    RestoreMetadata = new ProjectRestoreMetadata
                    {
                        ProjectStyle = ProjectStyle.PackageReference,
                        ProjectPath = ProjectPath,
                        ProjectUniqueName = ProjectPath,
                        OutputPath = Directory,
                        PackagesPath = Path.Combine(Directory, "packages"),
                    },
                };
                var graph = new DependencyGraphSpec();
                graph.AddProject(packageSpec);
                Evaluator.GetRestoreGraphAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(graph);

                var lockFile = new LockFile { Version = LockFileFormat.LegacyVersion };
                lockFile.Targets.Add(Target);
                new LockFileFormat().Write(Path.Combine(Directory, "project.assets.json"), lockFile);
            }

            public async Task<Project> AnalyzeAsync(ProjectAnalysisOptions options, string? analyzedPath = null)
            {
                var service = new ProjectAnalysisService(Evaluator, Substitute.For<IDotNetRestoreService>(), new FileSystem(), Logger);
                return Assert.Single(await service.AnalyzeProjectAsync(analyzedPath ?? ProjectPath, options));
            }

            public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
