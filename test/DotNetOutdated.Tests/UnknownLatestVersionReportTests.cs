#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

/// <summary>
/// The console report explains a dependency whose latest version is unknown by its cause, and only suggests a
/// connectivity problem when a package is unresolved or has no latest version for another reason.
/// </summary>
public class UnknownLatestVersionReportTests
{
    private const string ConnectivityHint = "Are you sure you can connect to all your configured NuGet servers?";

    [Fact]
    public async Task ARangeWithoutALowerBound_IsNamed_WithoutTheConnectivityHint()
    {
        var output = await Report(new OutdatedDependency { Dependency = Dependency("Contoso.Json", "(, 12.0.1]", "11.0.1") });

        Assert.Contains("The latest version of Contoso.Json can't be determined because its version range (, 12.0.1] has no lower bound.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectivityHint, output, StringComparison.Ordinal);
        Assert.DoesNotContain("DOTNET_HOST_PATH", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.4")]
    public async Task AnUnresolvedOrNotProvidedLatestVersion_ShowsTheConnectivityHint(string? resolvedVersion)
    {
        var output = await Report(new OutdatedDependency { Dependency = Dependency("Contoso.Http", "1.0.4", resolvedVersion) });

        Assert.Contains(ConnectivityHint, output, StringComparison.Ordinal);
        Assert.DoesNotContain("has no lower bound", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnresolvedPackageAlongsideAnUnboundedRange_ShowsBothMessages()
    {
        var output = await Report(
            new OutdatedDependency { Dependency = Dependency("Contoso.Http", "1.0.4", null) },
            new OutdatedDependency { Dependency = Dependency("Contoso.Json", "(, 12.0.1]", "11.0.1") });

        Assert.Contains(ConnectivityHint, output, StringComparison.Ordinal);
        Assert.Contains("Contoso.Json can't be determined because its version range (, 12.0.1] has no lower bound.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownLatestVersions_ShowNeitherMessage()
    {
        var output = await Report(new OutdatedDependency { Dependency = Dependency("Contoso.Http", "1.0.4", "1.0.4"), LatestVersion = NuGetVersion.Parse("2.0.0") });

        Assert.DoesNotContain(ConnectivityHint, output, StringComparison.Ordinal);
        Assert.DoesNotContain("has no lower bound", output, StringComparison.Ordinal);
    }

    private static Dependency Dependency(string name, string range, string? resolvedVersion) => new()
    {
        Name = name,
        VersionRange = VersionRange.Parse(range),
        ResolvedVersion = resolvedVersion == null ? null : NuGetVersion.Parse(resolvedVersion),
    };

    private static async Task<string> Report(params OutdatedDependency[] dependencies)
    {
        const string projectPath = "/src/App/App.csproj";
        var discovery = Substitute.For<IProjectDiscoveryService>();
        discovery.DiscoverProjects(projectPath, false, false).Returns(new List<string> { projectPath });
        var analysis = Substitute.For<IProjectAnalysisService>();
        analysis.AnalyzeProjectAsync(projectPath, Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<Project>());
        var outdated = Substitute.For<IOutdatedDependencyService>();
        outdated.FindOutdatedAsync(Arg.Any<IEnumerable<Project>>(), Arg.Any<OutdatedOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<OutdatedProject>
            {
                new()
                {
                    Name = "App",
                    FilePath = projectPath,
                    TargetFrameworks = [new OutdatedTargetFramework { Name = NuGetFramework.Parse("netstandard2.0"), Dependencies = dependencies }],
                },
            });

        var program = new Program(
            new FileSystem(), Substitute.For<IReporter>(), outdated, analysis, discovery,
            Substitute.For<IReferenceUpgrader>(), new DotNetRunnerOptions())
        {
            Path = projectPath,
        };

        var console = new RecordingConsole();
        Assert.Equal(0, await program.OnExecute(console));
        return console.Transcript;
    }
}
