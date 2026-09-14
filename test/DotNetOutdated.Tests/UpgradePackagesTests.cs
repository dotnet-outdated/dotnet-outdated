using System.Collections.Generic;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
using McMaster.Extensions.CommandLineUtils;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

public class UpgradePackagesTests
{
    [Fact]
    public void WhenUpgradeFails_TheProcessOutputIsWrittenToTheConsole()
    {
        // Arrange
        // dotnet add package reports NuGet restore/edit failures on stdout, not stderr, so a failed
        // upgrade must surface RunStatus.Output for the cause to be visible. A representative case:
        // the version is declared in an imported Directory.Build.props that the tool cannot edit.
        const string packageName = "Contoso.Widgets";
        string failureOutput =
            $"info : Adding PackageReference for package '{packageName}' into project 'MyProject.csproj'.\n" +
            $"error: Error while performing Update for package '{packageName}'. Cannot edit items in imported files -\n" +
            $"error:   Item 'PackageReference' for '{packageName}' in Imported file 'Directory.Build.props'.";

        var packageService = Substitute.For<IDotNetPackageService>();
        packageService
            .AddPackage(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<NuGetVersion>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new RunStatus(failureOutput, string.Empty, exitCode: 1));

        var program = new Program(
            Substitute.For<System.IO.Abstractions.IFileSystem>(),
            Substitute.For<IReporter>(),
            Substitute.For<INuGetPackageResolutionService>(),
            Substitute.For<IProjectAnalysisService>(),
            Substitute.For<IProjectDiscoveryService>(),
            packageService,
            new DotNetRunnerOptions())
        {
            Upgrade = (true, UpgradeType.Auto)
        };

        var dependency = new Dependency(packageName, VersionRange.Parse("1.0.0"), new NuGetVersion("1.0.0"), isAutoReferenced: false, isTransitive: false, isDevelopmentDependency: false);
        var targetFramework = new AnalyzedTargetFramework(NuGetFramework.Parse("net8.0"), [new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))]);
        var projects = new List<AnalyzedProject> { new("MyProject", "/projects/MyProject/app.cs", [targetFramework]) };

        using var console = new MockConsole();

        // Act
        bool success = program.UpgradePackages(projects, console);

        // Assert
        Assert.False(success);
        Assert.Contains(failureOutput, console.WrittenOut);
    }
}
