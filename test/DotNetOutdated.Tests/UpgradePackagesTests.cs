using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

    private readonly INuGetPackageInfoService _nuget = Substitute.For<INuGetPackageInfoService>();

    [Fact]
    public async Task WhenUpgradeFails_TheFailureDetailsAreWrittenToTheConsole()
    {
        // Arrange
        // A representative case: the version is declared in an imported Directory.Build.props that dotnet add can't edit.
        string failureDetails =
            $"info : Adding PackageReference for package '{PackageName}' into project 'MyProject.csproj'.\n" +
            $"error: Error while performing Update for package '{PackageName}'. Cannot edit items in imported files -\n" +
            $"error:   Item 'PackageReference' for '{PackageName}' in Imported file 'Directory.Build.props'.";
        UpgradeReturns(UpgradeOutcome.Failed, failureDetails);

        // Act
        (bool success, string written) = await RunUpgrade();

        // Assert
        Assert.False(success);
        Assert.Contains(failureDetails, written);
    }

    [Fact]
    public async Task WhenUpgradeFails_TheFailureDetailsFollowTheHeader_WithoutBlankLines()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Failed, "info : Restoring packages...\nerror: it went wrong");

        // Act
        (_, string written) = await RunUpgrade();

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
    public async Task WhenUpgradeFailsWithNoDetails_OnlyTheHeaderIsWritten()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Failed);

        // Act
        (_, string written) = await RunUpgrade();

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
    public async Task WhenUpgradeSucceeds_ItsWarningsAndOutcomeAreWritten(UpgradeOutcome outcome, string expectedLine)
    {
        // Arrange
        UpgradeReturns(outcome, warnings: ["the version expression was replaced"]);

        // Act
        (bool success, string written) = await RunUpgrade();

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
    public async Task EveryTargetFrameworkOfAPackage_IsUpgradedInOneCall()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        var dependency = new Dependency { Name = PackageName, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var targetFrameworks = new[] { "net8.0", "net9.0" }
            .Select(framework => new AnalyzedTargetFramework(NuGetFramework.Parse(framework), [new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))]))
            .ToList();

        // Act
        (bool success, string written) = await RunUpgradeOf([new("MyProject", ProjectPath, targetFrameworks)]);

        // Assert
        Assert.True(success);
        _upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade =>
            upgrade.Name == PackageName && upgrade.Version == new NuGetVersion("2.0.0") &&
            upgrade.Projects.SequenceEqual(new[] { new UpgradeProject(ProjectPath, "net8.0"), new UpgradeProject(ProjectPath, "net9.0") })));
        Assert.Single(Lines(written), line => line == "Project MyProject upgraded successfully");
    }

    [Fact]
    public async Task SdkAndPackageWithTheSameId_AreUpgradedSeparatelyByKind()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        var package = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var sdk = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0"), Kind = ReferenceKind.Sdk };

        // Act
        (bool success, string written) = await RunUpgrade(package, sdk);

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
    public async Task Prompt_NamesWhetherTheUpgradeIsAnSdkOrAPackage()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        var package = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
        var sdk = new Dependency { Name = "Cake.Sdk", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0"), Kind = ReferenceKind.Sdk };

        // Act
        (_, string written) = await RunUpgrade(UpgradeType.Prompt, "n\nn\n", package, sdk);

        // Assert
        string[] lines = Lines(written);
        Assert.Contains(lines, line => line.StartsWith("The package Cake.Sdk can be upgraded from 1.0.0 to 2.0.0."));
        Assert.Contains(lines, line => line.StartsWith("The SDK Cake.Sdk can be upgraded from 1.0.0 to 2.0.0."));
        Assert.Contains(lines, line => line.Contains("Do you wish to upgrade this package?"));
        Assert.Contains(lines, line => line.Contains("Do you wish to upgrade this SDK?"));
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public async Task Prompt_WhenThereIsNoInput_NothingIsUpgradedAndTheUserIsTold()
    {
        // Arrange: stdin closed or redirected from /dev/null (#831)
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (bool success, string written) = await RunUpgrade(UpgradeType.Prompt, string.Empty, Package("Contoso.A"), Package("Contoso.B"));

        // Assert
        Assert.True(success);
        string[] lines = Lines(written);
        Assert.Contains(lines, line => line.StartsWith("The package Contoso.A can be upgraded"));
        Assert.Single(lines, line => line == SkippedMessage);
        Assert.DoesNotContain(lines, line => line.StartsWith("The package Contoso.B can be upgraded"));
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public async Task Prompt_WhenTheInputEndsPartWay_OnlyTheAnsweredUpgradesAreMade()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (bool success, string written) = await RunUpgrade(UpgradeType.Prompt, "y\n", Package("Contoso.A"), Package("Contoso.B"), Package("Contoso.C"));

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
    public async Task Prompt_AnEmptyAnswerStillUpgrades()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);

        // Act
        (_, string written) = await RunUpgrade(UpgradeType.Prompt, "\n\n", Package("Contoso.A"), Package("Contoso.B"));

        // Assert
        Assert.DoesNotContain(SkippedMessage, written);
        _upgrader.Received(2).Upgrade(Arg.Any<ReferenceUpgrade>());
    }

    [Fact]
    public async Task ATargetFrameworkTheNewVersionDoesNotSupport_KeepsItsVersion_WhileTheOthersAreUpgraded()
    {
        // Arrange: a project that still targets net8.0 alongside net10.0, and a version that dropped net8.0.
        UpgradeReturns(UpgradeOutcome.Upgraded);
        UnsupportedOn("net8.0");

        // Act
        (bool success, string written) = await RunUpgradeOf([MultiTargeted("net8.0", "net10.0")]);

        // Assert
        Assert.True(success);
        _upgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade =>
            upgrade.Projects.SequenceEqual(new[] { new UpgradeProject(ProjectPath, "net10.0") }) &&
            upgrade.HeldBack.SequenceEqual(new[] { new UpgradeProject(ProjectPath, "net8.0") })));
        Assert.Contains(HeldBackMessage, Lines(written));
        Assert.Contains("Project MyProject upgraded successfully", Lines(written));
    }

    [Theory]
    [InlineData(UpgradeType.Auto)]
    [InlineData(UpgradeType.Prompt)]
    public async Task WhenTheNewVersionSupportsNoTargetFramework_NothingIsUpgradedOrAsked(UpgradeType upgradeType)
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        UnsupportedOn("net8.0");

        // Act
        (bool success, string written) = await RunUpgrade(upgradeType, string.Empty, Package(PackageName));

        // Assert
        Assert.True(success);
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
        Assert.Contains(HeldBackMessage, Lines(written));
        Assert.DoesNotContain("Do you wish", written);
    }

    [Fact]
    public async Task Prompt_ListsTheTargetFrameworksThatKeepTheirVersion()
    {
        // Arrange
        UpgradeReturns(UpgradeOutcome.Upgraded);
        UnsupportedOn("net8.0");

        // Act
        (_, string written) = await RunUpgradeOf([MultiTargeted("net8.0", "net10.0")], UpgradeType.Prompt, "n\n");

        // Assert
        string[] lines = Lines(written);
        Assert.Contains("The package Contoso.Widgets can be upgraded from 1.0.0 to 2.0.0. The following project(s) will be affected:", lines);
        Assert.Contains("» MyProject [net10.0]", lines);
        Assert.DoesNotContain("» MyProject [net8.0]", lines);
        Assert.Contains(HeldBackMessage, lines);
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public async Task AHeldBackTargetFrameworkThatSharesItsVersion_IsReportedAsUpgradedToo()
    {
        // Arrange
        SharesItsVersion(UpgradeOutcome.Upgraded);
        UnsupportedOn("net8.0");

        // Act
        (bool success, string written) = await RunUpgradeOf([MultiTargeted("net8.0", "net10.0")]);

        // Assert
        Assert.True(success);
        string[] lines = Lines(written);
        Assert.Contains(
            "Contoso.Widgets 2.0.0 doesn't support net8.0, but MyProject [net8.0] shares its version with a target framework that was upgraded, so it was upgraded too. " +
            "Declare the reference separately for each target framework to keep net8.0 on 1.0.0.",
            lines);
        Assert.DoesNotContain(HeldBackMessage, lines);
    }

    [Fact]
    public async Task Prompt_SaysAHeldBackTargetFrameworkThatSharesItsVersionWillBeUpgradedToo()
    {
        // Arrange
        SharesItsVersion(UpgradeOutcome.Upgraded);
        UnsupportedOn("net8.0");

        // Act
        (_, string written) = await RunUpgradeOf([MultiTargeted("net8.0", "net10.0")], UpgradeType.Prompt, "n\n");

        // Assert
        string[] lines = Lines(written);
        Assert.Contains("» MyProject [net10.0]", lines);
        Assert.Contains("» MyProject [net8.0]", lines);
        Assert.Contains(
            "Contoso.Widgets 2.0.0 doesn't support net8.0, but MyProject [net8.0] shares its version with a target framework being upgraded, so it will be upgraded too. " +
            "Declare the reference separately for each target framework to keep net8.0 on 1.0.0.",
            lines);
        Assert.DoesNotContain(HeldBackMessage, lines);
        _upgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
    }

    [Fact]
    public async Task AHeldBackTargetFrameworkThatSharesItsVersion_IsNotSaidToKeepIt_WhenItsUpgradeFails()
    {
        // Arrange
        SharesItsVersion(UpgradeOutcome.Failed);
        UnsupportedOn("net8.0");

        // Act
        (bool success, string written) = await RunUpgradeOf([MultiTargeted("net8.0", "net10.0")]);

        // Assert
        Assert.False(success);
        string[] lines = Lines(written);
        Assert.Contains("An error occurred while upgrading MyProject", lines);
        Assert.DoesNotContain(lines, line => line.Contains("doesn't support net8.0"));
    }

    // net8.0 shares its version with net10.0 (an unconditional reference, say), so the upgrader writes both.
    private void SharesItsVersion(UpgradeOutcome outcome)
    {
        _upgrader.FindHeldBackSharingVersion(Arg.Any<ReferenceUpgrade>()).Returns(call => call.Arg<ReferenceUpgrade>().HeldBack);
        _upgrader.Upgrade(Arg.Any<ReferenceUpgrade>()).Returns(call =>
        [
            new ProjectUpgradeResult
            {
                ProjectPath = ProjectPath,
                Outcome = outcome,
                HeldBackUpgraded = outcome == UpgradeOutcome.Upgraded ? call.Arg<ReferenceUpgrade>().HeldBack : [],
            },
        ]);
    }

    private const string HeldBackMessage =
        "Contoso.Widgets 2.0.0 doesn't support net8.0, so MyProject [net8.0] keeps 1.0.0. Move to a supported target framework to upgrade it.";

    private void UnsupportedOn(string targetFramework) =>
        _nuget.DeclaresTargetFrameworkUnsupportedAsync(Arg.Is<TargetFrameworkSupportQuery>(query =>
                query.TargetFramework == NuGetFramework.Parse(targetFramework)), Arg.Any<System.Threading.CancellationToken>())
            .Returns(true);

    private static AnalyzedProject MultiTargeted(params string[] targetFrameworks) =>
        new("MyProject", ProjectPath, targetFrameworks
            .Select(framework => new AnalyzedTargetFramework(NuGetFramework.Parse(framework), [new AnalyzedDependency(Package(PackageName), new NuGetVersion("2.0.0"))]))
            .ToList());

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

    private Task<(bool Success, string Written)> RunUpgrade() =>
        RunUpgrade(new Dependency { Name = PackageName, VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") });

    private Task<(bool Success, string Written)> RunUpgrade(params Dependency[] dependencies) =>
        RunUpgrade(UpgradeType.Auto, input: null, dependencies);

    private Task<(bool Success, string Written)> RunUpgrade(UpgradeType upgradeType, string input, params Dependency[] dependencies)
    {
        var targetFramework = new AnalyzedTargetFramework(
            NuGetFramework.Parse("net8.0"),
            dependencies.Select(dependency => new AnalyzedDependency(dependency, new NuGetVersion("2.0.0"))));
        return RunUpgradeOf([new AnalyzedProject("MyProject", ProjectPath, [targetFramework])], upgradeType, input);
    }

    private async Task<(bool Success, string Written)> RunUpgradeOf(List<AnalyzedProject> projects, UpgradeType upgradeType = UpgradeType.Auto, string input = null)
    {
        var program = new Program(
            Substitute.For<System.IO.Abstractions.IFileSystem>(),
            Substitute.For<IReporter>(),
            Substitute.For<IOutdatedDependencyService>(),
            Substitute.For<IProjectAnalysisService>(),
            Substitute.For<IProjectDiscoveryService>(),
            _upgrader,
            _nuget,
            new DotNetRunnerOptions())
        {
            Upgrade = (true, upgradeType)
        };

        using var console = new MockConsole(input);
        bool success = await program.UpgradePackagesAsync(projects, console);
        return (success, console.WrittenOut);
    }

    // MockConsole records colour changes as "[Colour]" markers; drop them and split into lines.
    private static string[] Lines(string written) =>
        Regex.Replace(written, @"\[[A-Za-z]+\]", string.Empty).Replace("\r\n", "\n").Split('\n');
}
