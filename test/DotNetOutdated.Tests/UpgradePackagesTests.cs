using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    private const string PackageName = "Contoso.Widgets";

    private readonly IDotNetPackageService _packageService = Substitute.For<IDotNetPackageService>();

    [Fact]
    public void WhenUpgradeFails_TheProcessOutputIsWrittenToTheConsole()
    {
        // Arrange
        // dotnet add package reports NuGet restore/edit failures on stdout, not stderr, so a failed
        // upgrade must surface RunStatus.Output for the cause to be visible. A representative case:
        // the version is declared in an imported Directory.Build.props that the tool cannot edit.
        string failureOutput =
            $"info : Adding PackageReference for package '{PackageName}' into project 'MyProject.csproj'.\n" +
            $"error: Error while performing Update for package '{PackageName}'. Cannot edit items in imported files -\n" +
            $"error:   Item 'PackageReference' for '{PackageName}' in Imported file 'Directory.Build.props'.";
        AddPackageReturns(new RunStatus(failureOutput, string.Empty, exitCode: 1));

        // Act
        (bool success, string written) = RunUpgrade();

        // Assert
        Assert.False(success);
        Assert.Contains(failureOutput, written);
    }

    [Fact]
    public void WhenUpgradeFails_OutputIsWrittenBeforeErrors_WithoutBlankLines()
    {
        // Arrange
        AddPackageReturns(new RunStatus("info : Restoring packages...\n", "error: it went wrong\n", exitCode: 1));

        // Act
        (_, string written) = RunUpgrade();

        // Assert
        string[] lines = Lines(written);
        string[] expected =
        [
            $"Upgrading package {PackageName}...",
            "An error occurred while upgrading MyProject",
            "info : Restoring packages...",
            "error: it went wrong",
            "", // the blank line that separates packages
            "",
        ];
        Assert.Equal(expected, lines.Skip(1).ToArray());
    }

    [Fact]
    public void WhenUpgradeFailsWithNoOutput_OnlyTheHeaderIsWritten()
    {
        // Arrange
        AddPackageReturns(new RunStatus("  \n", string.Empty, exitCode: 1));

        // Act
        (_, string written) = RunUpgrade();

        // Assert
        string[] expected =
        [
            $"Upgrading package {PackageName}...",
            "An error occurred while upgrading MyProject",
            "", // the blank line that separates packages
            "",
        ];
        Assert.Equal(expected, Lines(written).Skip(1).ToArray());
    }

    [Fact]
    public void WhenUpgradeSucceeds_TheProcessOutputIsNotWritten()
    {
        // Arrange
        AddPackageReturns(new RunStatus("info : restore log", string.Empty, exitCode: 0));

        // Act
        (bool success, string written) = RunUpgrade();

        // Assert
        Assert.True(success);
        Assert.DoesNotContain("restore log", written);
    }

    private void AddPackageReturns(RunStatus status) =>
        _packageService
            .AddPackage(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VersionRange>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(status);

    private (bool Success, string Written) RunUpgrade()
    {
        var program = new Program(
            Substitute.For<System.IO.Abstractions.IFileSystem>(),
            Substitute.For<IReporter>(),
            Substitute.For<INuGetPackageResolutionService>(),
            Substitute.For<IProjectAnalysisService>(),
            Substitute.For<IProjectDiscoveryService>(),
            _packageService,
            new DotNetRunnerOptions())
        {
            Upgrade = (true, UpgradeType.Auto)
        };

        var dependency = new Dependency(PackageName, VersionRange.Parse("1.0.0"), new NuGetVersion("1.0.0"), isAutoReferenced: false, isTransitive: false, isDevelopmentDependency: false);
        var targetFramework = new AnalyzedTargetFramework(NuGetFramework.Parse("net8.0"), [new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))]);
        // A file-based app path keeps the test off the file system: for a .csproj, IsProjectSdkStyle()
        // would read the project file to decide whether to remove the package before adding it.
        var projects = new List<AnalyzedProject> { new("MyProject", "/projects/MyProject/app.cs", [targetFramework]) };

        using var console = new MockConsole();
        bool success = program.UpgradePackages(projects, console);
        return (success, console.WrittenOut);
    }

    // MockConsole records colour changes as "[Colour]" markers; drop them and split into lines.
    private static string[] Lines(string written) =>
        Regex.Replace(written, @"\[[A-Za-z]+\]", string.Empty).Replace("\r\n", "\n").Split('\n');
}
