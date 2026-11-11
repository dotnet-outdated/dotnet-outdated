#nullable enable

using System;
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
    }
}
