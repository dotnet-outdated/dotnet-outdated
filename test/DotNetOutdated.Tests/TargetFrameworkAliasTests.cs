using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

public class TargetFrameworkAliasTests
{
    [Theory]
    [InlineData("net10.0-windows10.0.19041.0", "net10.0-windows10.0.19041")]
    [InlineData("net10.0-android", "net10.0-android36.0")]
    [InlineData("desktop", "net10.0-windows10.0.19041")]
    [InlineData(null, "net8.0")]
    [InlineData("", "net8.0")]
    public async Task UpgradesUseTheDeclaredTargetFrameworkAlias(string targetAlias, string frameworkName)
    {
        const string packageName = "Contoso.Widgets";
        var installedVersion = new NuGetVersion("1.0.0");
        var latestVersion = new NuGetVersion("2.0.0");
        var framework = NuGetFramework.Parse(frameworkName);
        string expectedAlias = string.IsNullOrEmpty(targetAlias) ? framework.GetShortFolderName() : targetAlias;

        string directory = Path.Combine(Path.GetTempPath(), "dotnet-outdated-target-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string projectPath = Path.Combine(directory, "MyProject.csproj");
            await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var frameworkInformation = new TargetFrameworkInformation
            {
                FrameworkName = framework,
                TargetAlias = targetAlias,
                Dependencies =
                [
                    new LibraryDependency
                    {
                        LibraryRange = new LibraryRange(packageName, new VersionRange(installedVersion), LibraryDependencyTarget.Package),
                    },
                ],
            };
            var packageSpec = new PackageSpec([frameworkInformation])
            {
                Name = "MyProject",
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

            // Analysis reads the real assets file, while the graph and package lookup stay in process.
            var target = new LockFileTarget { TargetFramework = framework };
            target.Libraries.Add(new LockFileTargetLibrary { Name = packageName, Version = installedVersion, Type = "package" });
            var lockFile = new LockFile { Version = LockFileFormat.LegacyVersion };
            lockFile.Targets.Add(target);
            new LockFileFormat().Write(Path.Combine(directory, "project.assets.json"), lockFile);

            var graphService = Substitute.For<IDependencyGraphService>();
            graphService.GenerateDependencyGraphAsync(projectPath, string.Empty).Returns(graph);
            var fileSystem = new FileSystem();
            var analysisService = new ProjectAnalysisService(graphService, Substitute.For<IDotNetRestoreService>(), fileSystem);
            var discoveryService = Substitute.For<IProjectDiscoveryService>();
            discoveryService.DiscoverProjects(projectPath, false, false).Returns(new List<string> { projectPath });
            var resolutionService = Substitute.For<INuGetPackageResolutionService>();
            resolutionService.ResolveLatestVersionAsync(
                Arg.Is<LatestVersionQuery>(query =>
                    query.PackageId == packageName && query.CurrentVersion == installedVersion &&
                    query.TargetFramework == framework && query.ProjectFilePath == projectPath),
                Arg.Any<CancellationToken>()).Returns(latestVersion);
            var upgrader = Substitute.For<IReferenceUpgrader>();
            upgrader.Upgrade(Arg.Any<ReferenceUpgrade>())
                .Returns(call => call.Arg<ReferenceUpgrade>().Projects
                    .Select(project => new ProjectUpgradeResult { ProjectPath = project.Path, Outcome = UpgradeOutcome.Upgraded })
                    .ToList());
            var program = new Program(
                fileSystem, Substitute.For<IReporter>(), new OutdatedDependencyService(discoveryService, analysisService, resolutionService, fileSystem), analysisService,
                discoveryService, upgrader, Substitute.For<INuGetPackageInfoService>(), new DotNetRunnerOptions())
            {
                Path = projectPath,
                Upgrade = (true, UpgradeType.Auto),
            };

            using var console = new MockConsole();
            int exitCode = await program.OnExecute(console);

            Assert.Equal(0, exitCode);
            upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade =>
                upgrade.Projects.Single() == new UpgradeProject(projectPath, expectedAlias) && upgrade.Name == packageName &&
                upgrade.Version == latestVersion && !upgrade.NoRestore && !upgrade.IgnoreFailedSources));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
