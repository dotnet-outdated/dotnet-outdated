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

    private const string ProjectPath = "/projects/MyProject/MyProject.csproj";

    private readonly IReferenceUpgrader _upgrader = Substitute.For<IReferenceUpgrader>();

    [Fact]
    public void WhenUpgradeFails_TheFailureDetailsAreWrittenToTheConsole()
    {
        // Arrange
        // A representative case: the version is declared in an imported Directory.Build.props that dotnet add can't edit.
        string failureDetails =
            $"info : Adding PackageReference for package '{PackageName}' into project 'MyProject.csproj'.\n" +
            $"error: Error while performing Update for package '{PackageName}'. Cannot edit items in imported files -\n" +
            $"error:   Item 'PackageReference' for '{PackageName}' in Imported file 'Directory.Build.props'.";
        UpgradeReturns(UpgradeOutcome.Failed, failureDetails);

        // Act
        (bool success, string written) = RunUpgrade();

        // Assert
        Assert.False(success);
        Assert.Contains(failureDetails, written);
    }

    [Fact]
    public void WhenUpgradeFails_TheFailureDetailsFollowTheHeader_WithoutBlankLines()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Failed, "info : Restoring packages...\nerror: it went wrong");

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
    public void WhenUpgradeFailsWithNoDetails_OnlyTheHeaderIsWritten()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Failed);

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

    [Theory]
    [InlineData(UpgradeOutcome.Upgraded, "Project MyProject upgraded successfully")]
    [InlineData(UpgradeOutcome.AlreadyCurrent, "Project MyProject already has Contoso.Widgets 2.0.0")]
    public void WhenUpgradeSucceeds_ItsWarningsAndOutcomeAreWritten(UpgradeOutcome outcome, string expectedLine)
    {
        // Arrange
        UpgradeReturns(outcome, warnings: ["the version expression was replaced"]);

        // Act
        (bool success, string written) = RunUpgrade();

        // Assert
        Assert.True(success);
        string[] expected =
        [
            $"Upgrading package {PackageName}...",
            "the version expression was replaced",
            expectedLine,
            "",
            "",
        ];
        Assert.Equal(expected, Lines(written).Skip(1).ToArray());
    }

    [Fact]
    public void EveryTargetFrameworkOfAPackage_IsUpgradedInOneCall()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        var dependency = new Dependency { Name = PackageName, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var targetFrameworks = new[] { "net8.0", "net9.0" }
            .Select(framework => new AnalyzedTargetFramework(NuGetFramework.Parse(framework), [new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))]))
            .ToList();

        // Act
        (bool success, string written) = RunUpgradeOf([new("MyProject", ProjectPath, targetFrameworks)]);

        // Assert
        Assert.True(success);
        _upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade =>
            upgrade.Name == PackageName && upgrade.Version == new NuGetVersion("2.0.0") &&
            upgrade.Projects.SequenceEqual(new[] { new UpgradeProject(ProjectPath, "net8.0"), new UpgradeProject(ProjectPath, "net9.0") })));
        Assert.Single(Lines(written), line => line == "Project MyProject upgraded successfully");
    }

    [Fact]
    public void SdkAndPackageWithTheSameId_AreUpgradedSeparatelyByKind()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
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
            _upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Name == "Cake.Sdk" && upgrade.Kind == kind));
        }
    }

    [Fact]
    public void Prompt_NamesWhetherTheUpgradeIsAnSdkOrAPackage()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
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
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public void Prompt_WhenThereIsNoInput_NothingIsUpgradedAndTheUserIsTold()
    {
        // Arrange: stdin closed or redirected from /dev/null (#831)
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (bool success, string written) = RunUpgrade(UpgradeType.Prompt, string.Empty, Package("Contoso.A"), Package("Contoso.B"));

        // Assert
        Assert.True(success);
        string[] lines = Lines(written);
        Assert.Contains(lines, line => line.StartsWith("The package Contoso.A can be upgraded"));
        Assert.Single(lines, line => line == SkippedMessage);
        Assert.DoesNotContain(lines, line => line.StartsWith("The package Contoso.B can be upgraded"));
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public void Prompt_WhenTheInputEndsPartWay_OnlyTheAnsweredUpgradesAreMade()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (bool success, string written) = RunUpgrade(UpgradeType.Prompt, "y\n", Package("Contoso.A"), Package("Contoso.B"), Package("Contoso.C"));

        // Assert
        Assert.True(success);
        string[] lines = Lines(written);
        Assert.Contains(lines, line => line.EndsWith("Upgrading package Contoso.A..."));
        Assert.Contains(lines, line => line.StartsWith("The package Contoso.B can be upgraded"));
        Assert.Single(lines, line => line == SkippedMessage);
        Assert.DoesNotContain(lines, line => line.StartsWith("The package Contoso.C can be upgraded"));
        _upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Name == "Contoso.A"));
        _upgrader.Received(1).Upgrade(Arg.Any<ReferenceUpgrade>());
    }

    [Fact]
    public void Prompt_AnEmptyAnswerStillUpgrades()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (_, string written) = RunUpgrade(UpgradeType.Prompt, "\n\n", Package("Contoso.A"), Package("Contoso.B"));

        // Assert
        Assert.DoesNotContain(SkippedMessage, written);
        _upgrader.Received(2).Upgrade(Arg.Any<ReferenceUpgrade>());
    }

    private const string SkippedMessage = "No input is available to answer the prompt, so the remaining upgrades were skipped.";

    private static Dependency Package(string name) =>
        new() { Name = name, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };

    private void UpgradeReturns(UpgradeOutcome outcome, string failureDetails = "", string[] warnings = null) =>
        _upgrader.Upgrade(Arg.Any<ReferenceUpgrade>()).Returns(call => call.Arg<ReferenceUpgrade>().Projects
            .Select(project => project.Path)
            .Distinct()
            .Select(path => new ProjectUpgradeResult
            {
                ProjectPath = path,
                Outcome = outcome,
                FailureDetails = failureDetails,
                Warnings = warnings ?? [],
            })
            .ToList());

    private (bool Success, string Written) RunUpgrade() =>
        RunUpgrade(new Dependency { Name = PackageName, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") });

    private (bool Success, string Written) RunUpgrade(params Dependency[] dependencies) =>
        RunUpgrade(UpgradeType.Auto, input: null, dependencies);

    private (bool Success, string Written) RunUpgrade(UpgradeType upgradeType, string input, params Dependency[] dependencies)
    {
        var targetFramework = new AnalyzedTargetFramework(
            NuGetFramework.Parse("net8.0"),
            dependencies.Select(dependency => new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))));
        return RunUpgradeOf([new AnalyzedProject("MyProject", ProjectPath, [targetFramework])], upgradeType, input);
    }

    private (bool Success, string Written) RunUpgradeOf(List<AnalyzedProject> projects, UpgradeType upgradeType = UpgradeType.Auto, string input = null)
    {
        var program = new Program(
            Substitute.For<System.IO.Abstractions.IFileSystem>(),
            Substitute.For<IReporter>(),
            Substitute.For<IOutdatedDependencyService>(),
            Substitute.For<IProjectAnalysisService>(),
            Substitute.For<IProjectDiscoveryService>(),
            _upgrader,
            new DotNetRunnerOptions())
        {
            Upgrade = (true, upgradeType)
        };

        using var console = new MockConsole(input);
        bool success = program.UpgradePackages(projects, console);
        return (success, console.WrittenOut);
    }

    // MockConsole records colour changes as "[Colour]" markers; drop them and split into lines.
    private static string[] Lines(string written) =>
        Regex.Replace(written, @"\[[A-Za-z]+\]", string.Empty).Replace("\r\n", "\n").Split('\n');
}
