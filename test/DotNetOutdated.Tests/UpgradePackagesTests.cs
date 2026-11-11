using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
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
        UpgradeReturns(new RunStatus(failureOutput, string.Empty, exitCode: 1));

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
        UpgradeReturns(new RunStatus("info : Restoring packages...\n", "error: it went wrong\n", exitCode: 1));

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
        UpgradeReturns(new RunStatus("  \n", string.Empty, exitCode: 1));

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
        UpgradeReturns(new RunStatus("info : restore log", string.Empty, exitCode: 0));

        // Act
        (bool success, string written) = RunUpgrade();

        // Assert
        Assert.True(success);
        Assert.DoesNotContain("restore log", written);
    }

    [Fact]
    public void SdkAndPackageWithTheSameId_AreUpgradedSeparatelyByKind()
    {
        // Arrange
        UpgradeReturns(new RunStatus(string.Empty, string.Empty, exitCode: 0));
        var package = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var sdk = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0"), Kind = ReferenceKind.Sdk };

        // Act
        (bool success, string written) = RunUpgrade(package, sdk);

        // Assert
        Assert.True(success);
        string[] lines = Lines(written);
        Assert.Contains("Upgrading package Cake.Sdk...", lines);
        Assert.Contains("Upgrading SDK Cake.Sdk...", lines);
        foreach (var kind in new[] { ReferenceKind.Package, ReferenceKind.Sdk })
        {
            _packageService.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Name == "Cake.Sdk" && upgrade.Kind == kind));
        }
    }

    [Fact]
    public void Prompt_NamesWhetherTheUpgradeIsAnSdkOrAPackage()
    {
        // Arrange
        UpgradeReturns(new RunStatus(string.Empty, string.Empty, exitCode: 0));
        var package = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var sdk = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0"), Kind = ReferenceKind.Sdk };

        // Act
        (_, string written) = RunUpgrade(UpgradeType.Prompt, "n\nn\n", package, sdk);

        // Assert
        string[] lines = Lines(written);
        Assert.Contains(lines, line => line.StartsWith("The package Cake.Sdk can be upgraded from 1.0.0 to 2.0.0."));
        Assert.Contains(lines, line => line.StartsWith("The SDK Cake.Sdk can be upgraded from 1.0.0 to 2.0.0."));
        Assert.Contains(lines, line => line.Contains("Do you wish to upgrade this package?"));
        Assert.Contains(lines, line => line.Contains("Do you wish to upgrade this SDK?"));
        _packageService.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    private void UpgradeReturns(RunStatus status) =>
        _packageService.Upgrade(Arg.Any<ReferenceUpgrade>()).Returns(status);

    private (bool Success, string Written) RunUpgrade() =>
        RunUpgrade(new Dependency { Name = PackageName, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") });

    private (bool Success, string Written) RunUpgrade(params Dependency[] dependencies) =>
        RunUpgrade(UpgradeType.Auto, input: null, dependencies);

    private (bool Success, string Written) RunUpgrade(UpgradeType upgradeType, string input, params Dependency[] dependencies)
    {
        var program = new Program(
            Substitute.For<System.IO.Abstractions.IFileSystem>(),
            Substitute.For<IReporter>(),
            Substitute.For<IOutdatedDependencyService>(),
            Substitute.For<IProjectAnalysisService>(),
            Substitute.For<IProjectDiscoveryService>(),
            _packageService,
            new DotNetRunnerOptions())
        {
            Upgrade = (true, upgradeType)
        };

        var targetFramework = new AnalyzedTargetFramework(
            NuGetFramework.Parse("net8.0"),
            dependencies.Select(dependency => new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))));
        // A file-based app path keeps the test off the file system: for a .csproj, IsProjectSdkStyle()
        // would read the project file to decide whether to remove the package before adding it.
        var projects = new List<AnalyzedProject> { new("MyProject", "/projects/MyProject/app.cs", [targetFramework]) };

        using var console = new MockConsole(input);
        bool success = program.UpgradePackages(projects, console);
        return (success, console.WrittenOut);
    }

    // MockConsole records colour changes as "[Colour]" markers; drop them and split into lines.
    private static string[] Lines(string written) =>
        Regex.Replace(written, @"\[[A-Za-z]+\]", string.Empty).Replace("\r\n", "\n").Split('\n');
}
