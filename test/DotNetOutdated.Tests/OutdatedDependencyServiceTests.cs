#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    /// <summary>
    /// The outdated report for a path: its projects found and analyzed, and the latest version of each dependency. The
    /// projects are found on a mock file system, analysis is a substitute, and so is the latest version lookup.
    /// </summary>
    public class OutdatedDependencyServiceTests
    {
        private static readonly NuGetFramework Net9 = NuGetFramework.Parse("net9.0");
        private static readonly string Root = XFS.Path(@"c:\src");

        private readonly MockFileSystem _fileSystem = new();
        private readonly IProjectAnalysisService _analysis = Substitute.For<IProjectAnalysisService>();
        private readonly Dictionary<string, IReadOnlyList<Project>> _analyzed = [];
        private readonly INuGetPackageResolutionService _resolution = Substitute.For<INuGetPackageResolutionService>();
        private readonly Dictionary<string, string> _latestVersions = new(StringComparer.OrdinalIgnoreCase);

        public OutdatedDependencyServiceTests()
        {
            _analysis.AnalyzeProjectAsync(Arg.Any<string>(), Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => _analyzed[call.ArgAt<string>(0)]);

            _resolution.ResolveLatestVersionAsync(Arg.Any<LatestVersionQuery>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var query = call.Arg<LatestVersionQuery>();
                    return _latestVersions.TryGetValue(query.PackageId, out var latest) ? NuGetVersion.Parse(latest) : query.CurrentVersion;
                });
        }

        [Fact]
        public async Task ReportsOnlyDependenciesWithANewerVersion_OrderedByName()
        {
            _latestVersions["Zeta"] = "2.0.0";
            _latestVersions["Alpha"] = "1.1.0";
            var project = Project("App", Dependency("Zeta", "1.0.0"), Dependency("Current", "1.0.0"), Dependency("Alpha", "1.0.0"));

            var result = await Find([project]);

            var dependencies = Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies;
            Assert.Equal(["Alpha", "Zeta"], dependencies.Select(d => d.Dependency.Name));
            Assert.Equal(DependencyUpgradeSeverity.Minor, dependencies[0].UpgradeSeverity);
            Assert.Equal(DependencyUpgradeSeverity.Major, dependencies[1].UpgradeSeverity);
        }

        [Fact]
        public async Task AProjectWithNothingToReport_IsStillReported_WithEveryTargetFramework()
        {
            var project = Project("App", Dependency("Current", "1.0.0")) with
            {
                TargetFrameworks =
                [
                    new TargetFramework { Name = Net9, Dependencies = [Dependency("Current", "1.0.0")] },
                    new TargetFramework { Name = NuGetFramework.Parse("netstandard2.0") },
                ],
            };

            var result = await Find([project]);

            var reported = Assert.Single(result);
            Assert.Equal(["net9.0", "netstandard2.0"], reported.TargetFrameworks.Select(f => f.TargetAlias));
            Assert.All(reported.TargetFrameworks, f => Assert.Empty(f.Dependencies));
        }

        [Fact]
        public async Task AProject_IsReportedWithThePackageSourcesItRestoresFrom()
        {
            Uri[] sources = [new("https://api.nuget.org/v3/index.json"), new("https://pkgs.contoso.com/v3/index.json")];

            var result = await Find([Project("App", Dependency("Package", "1.0.0")) with { Sources = sources }]);

            Assert.Equal(sources, Assert.Single(result).Sources);
        }

        [Theory]
        [InlineData(false, new[] { "App" })]
        [InlineData(true, new[] { "App", "tool" })]
        public async Task FileBasedApps_AreFoundOnlyWhenIncluded(bool includeFileBasedApps, string[] expected)
        {
            var app = Project("App", Dependency("Package", "1.0.0"));
            var tool = Project("tool", Dependency("Package", "1.0.0")) with { FilePath = XFS.Path(@"c:\src\tools\tool.cs") };

            var result = await Find([app, tool], new OutdatedOptions { IncludeFileBasedApps = includeFileBasedApps });

            Assert.Equal(expected, result.Select(p => p.Name));
        }

        [Fact]
        public async Task EachProject_IsAnalyzedWithTheTransitiveAndRuntimeSettings()
        {
            await Find([Project("App", Dependency("Package", "1.0.0"))], new OutdatedOptions
            {
                IncludeTransitiveDependencies = true,
                TransitiveDepth = 3,
                Runtime = "linux-x64",
            });

            await _analysis.Received(1).AnalyzeProjectAsync(
                XFS.Path(@"c:\src\App\App.csproj"),
                Arg.Is<ProjectAnalysisOptions>(o => o.IncludeTransitiveDependencies && o.TransitiveDepth == 3 && o.Runtime == "linux-x64" && !o.RunRestore),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AProjectAnotherReferences_IsReportedOnce()
        {
            // A project's restore graph holds the projects it references, so a referenced project is analyzed both on
            // its own and with each project that references it (issue #830).
            var app = Project("App", Dependency("Package", "1.0.0"));
            var lib = Project("Lib", Dependency("Package", "1.0.0"));
            AddProjectFile(app.FilePath);
            AddProjectFile(lib.FilePath);
            _analyzed[app.FilePath] = [app, lib];
            _analyzed[lib.FilePath] = [lib];

            var report = await Service().FindOutdatedAsync(Root, new OutdatedOptions { Recursive = true });

            Assert.Equal(["App", "Lib"], report.Projects.Select(p => p.Name));
        }

        [Fact]
        public async Task AProjectReachedThroughASymbolicLink_IsReportedOnce()
        {
            // MSBuild reports a referenced project by its real path, while the project found at the path keeps the
            // link (on macOS the temp folder /var is a link to /private/var).
            var app = Project("App", Dependency("Package", "1.0.0"));
            var lib = Project("Lib", Dependency("Package", "1.0.0"));
            AddProjectFile(app.FilePath);
            AddProjectFile(lib.FilePath);
            var linked = XFS.Path(@"c:\linked");
            _fileSystem.Directory.CreateSymbolicLink(linked, Root);
            _analyzed[app.FilePath] = [app, lib with { FilePath = XFS.Path(@"c:\linked\Lib\Lib.csproj") }];
            _analyzed[lib.FilePath] = [lib];

            var report = await Service().FindOutdatedAsync(Root, new OutdatedOptions { Recursive = true });

            Assert.Equal(["App", "Lib"], report.Projects.Select(p => p.Name));
        }

        [Fact]
        public async Task EachStage_IsReportedAsItStarts()
        {
            AddProjectFile(XFS.Path(@"c:\src\App\App.csproj"));
            var events = new List<string>();
            _analysis.AnalyzeProjectAsync(Arg.Any<string>(), Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    events.Add("analysis");
                    return [Project("App", Dependency("Package", "1.0.0"))];
                });
            _resolution.ResolveLatestVersionAsync(Arg.Any<LatestVersionQuery>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    events.Add("lookup");
                    return call.Arg<LatestVersionQuery>().CurrentVersion;
                });

            await Service().FindOutdatedAsync(Root, new OutdatedOptions { Recursive = true }, new SynchronousProgress(stage => events.Add(stage.ToString())));

            Assert.Equal(["DiscoveringProjects", "AnalyzingProjects", "analysis", "FindingLatestVersions", "lookup"], events);
        }

        [Fact]
        public async Task AMissingPath_IsAValidationError_AndNothingIsAnalyzed()
        {
            await Assert.ThrowsAsync<CommandValidationException>(() => Service().FindOutdatedAsync(XFS.Path(@"c:\missing")));

            await _analysis.DidNotReceiveWithAnyArgs().AnalyzeProjectAsync(default!, default, default);
        }

        [Fact]
        public async Task IncludeUpToDate_ReportsEveryDependency()
        {
            var result = await Find(
                [Project("App", Dependency("Current", "1.0.0"))],
                new OutdatedOptions { IncludeUpToDate = true });

            var dependency = Assert.Single(Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies);
            Assert.Equal(DependencyUpgradeSeverity.None, dependency.UpgradeSeverity);
        }

        [Theory]
        [InlineData(false, new string[0])]
        [InlineData(true, new[] { "Implicit" })]
        public async Task AutoReferencedDependencies_AreReportedOnlyWhenIncluded(bool includeAutoReferences, string[] expected)
        {
            _latestVersions["Implicit"] = "2.0.0";
            var project = Project("App", Dependency("Implicit", "1.0.0", isAutoReferenced: true));

            var result = await Find([project], new OutdatedOptions { IncludeAutoReferences = includeAutoReferences });

            Assert.Equal(expected, result.SelectMany(p => p.TargetFrameworks).SelectMany(f => f.Dependencies).Select(d => d.Dependency.Name));
        }

        [Fact]
        public async Task Filters_MatchPartOfTheNameIgnoringCase()
        {
            foreach (var name in new[] { "Contoso.Core", "Contoso.Tests", "Other" })
            {
                _latestVersions[name] = "2.0.0";
            }

            var project = Project("App", Dependency("Contoso.Core", "1.0.0"), Dependency("Contoso.Tests", "1.0.0"), Dependency("Other", "1.0.0"));

            var result = await Find([project], new OutdatedOptions
            {
                IncludeFilters = ["contoso"],
                ExcludeFilters = ["TESTS"],
            });

            Assert.Equal(["Contoso.Core"], result.Single().TargetFrameworks.Single().Dependencies.Select(d => d.Dependency.Name));
        }

        [Fact]
        public async Task SeveralIncludeFilters_ReportDependenciesMatchingAnyOfThem()
        {
            foreach (var name in new[] { "Contoso.Core", "Fabrikam.Core", "Other" })
            {
                _latestVersions[name] = "2.0.0";
            }

            var project = Project("App", Dependency("Contoso.Core", "1.0.0"), Dependency("Fabrikam.Core", "1.0.0"), Dependency("Other", "1.0.0"));

            var result = await Find([project], new OutdatedOptions
            {
                IncludeFilters = ["Contoso", "Fabrikam"],
            });

            Assert.Equal(["Contoso.Core", "Fabrikam.Core"], result.Single().TargetFrameworks.Single().Dependencies.Select(d => d.Dependency.Name));
        }

        [Fact]
        public async Task Projects_AreOrderedByNameThenPath_AndFrameworksByFrameworkIdentifier()
        {
            _latestVersions["Package"] = "2.0.0";
            Project ProjectAt(string name, string filePath, params string[] frameworks) => Project(name, Dependency("Package", "1.0.0")) with
            {
                FilePath = filePath,
                TargetFrameworks =
                [
                    .. frameworks.Select(framework => new TargetFramework
                    {
                        Name = NuGetFramework.Parse(framework),
                        TargetAlias = framework,
                        Dependencies = [Dependency("Package", "1.0.0")],
                    }),
                ],
            };

            var result = await Find(
            [
                ProjectAt("Beta", XFS.Path(@"c:\src\a\Beta.csproj"), "net9.0"),
                ProjectAt("Alpha", XFS.Path(@"c:\src\b\Alpha.csproj"), "net9.0"),
                ProjectAt("Alpha", XFS.Path(@"c:\src\a\Alpha.csproj"), "netstandard2.0", "net9.0"),
            ]);

            Assert.Equal([XFS.Path(@"c:\src\a\Alpha.csproj"), XFS.Path(@"c:\src\b\Alpha.csproj"), XFS.Path(@"c:\src\a\Beta.csproj")], result.Select(p => p.FilePath));
            Assert.Equal(["net9.0", "netstandard2.0"], result[0].TargetFrameworks.Select(f => f.TargetAlias));
        }

        [Fact]
        public async Task MaximumVersion_CapsTheAllowedRangeWithoutWideningIt()
        {
            _latestVersions["Open"] = "2.0.0";
            _latestVersions["Bounded"] = "2.0.0";
            var project = Project("App", Dependency("Open", "1.0.0"), Dependency("Bounded", "[1.0.0, 1.5.0)"));

            await Find([project], new OutdatedOptions { MaximumVersion = NuGetVersion.Parse("3.0.0") });

            await _resolution.Received(1).ResolveLatestVersionAsync(
                Arg.Is<LatestVersionQuery>(q => q.PackageId == "Open" && q.AllowedRange.MaxVersion == NuGetVersion.Parse("3.0.0") && q.AllowedRange.IsMaxInclusive),
                Arg.Any<CancellationToken>());
            await _resolution.Received(1).ResolveLatestVersionAsync(
                Arg.Is<LatestVersionQuery>(q => q.PackageId == "Bounded" && q.AllowedRange.MaxVersion == NuGetVersion.Parse("1.5.0") && !q.AllowedRange.IsMaxInclusive),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task PassesTheVersionSettingsToTheLookup()
        {
            var project = Project("App", Dependency("Package", "1.0.0-beta.1", isDevelopmentDependency: true));

            await Find([project], new OutdatedOptions
            {
                VersionLock = VersionLock.Minor,
                Prerelease = PrereleaseReporting.Always,
                PrereleaseLabel = "beta",
                OlderThanDays = 7,
                IgnoreFailedSources = true,
            });

            await _resolution.Received(1).ResolveLatestVersionAsync(
                Arg.Is<LatestVersionQuery>(q =>
                    q.PackageId == "Package" && q.CurrentVersion == NuGetVersion.Parse("1.0.0-beta.1") &&
                    q.TargetFramework == Net9 && q.ProjectFilePath == XFS.Path(@"c:\src\App\App.csproj") && q.IsDevelopmentDependency &&
                    q.VersionLock == VersionLock.Minor && q.Prerelease == PrereleaseReporting.Always && q.PrereleaseLabel == "beta" &&
                    q.OlderThanDays == 7 && q.IgnoreFailedSources),
                Arg.Any<CancellationToken>());
        }

        [Theory]
        [InlineData(null, "1.0.0", LatestVersionUnknownReason.Unresolved)]
        [InlineData(null, "(, 2.0.0)", LatestVersionUnknownReason.Unresolved)]
        [InlineData("1.0.0", "(, 2.0.0)", LatestVersionUnknownReason.NoLowerBound)]
        public async Task UnresolvedOrUnboundedDependencies_AreReportedWithAnUnknownLatestVersion(string? resolvedVersion, string range, LatestVersionUnknownReason reason)
        {
            var dependency = new Dependency { Name = "Package", VersionRange = VersionRange.Parse(range), ResolvedVersion = resolvedVersion == null ? null : NuGetVersion.Parse(resolvedVersion) };

            var result = await Find([Project("App", dependency)]);

            var outdated = Assert.Single(Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies);
            Assert.Null(outdated.LatestVersion);
            Assert.Equal(DependencyUpgradeSeverity.Unknown, outdated.UpgradeSeverity);
            Assert.Equal(reason, outdated.LatestVersionUnknownReason);
            await _resolution.DidNotReceiveWithAnyArgs().ResolveLatestVersionAsync(default!, default);
        }

        [Fact]
        public async Task AnUnboundedRangeCappedAtTheMaximumVersion_IsStillNotLookedUp()
        {
            var dependency = new Dependency { Name = "Package", VersionRange = VersionRange.Parse("(, 2.0.0)"), ResolvedVersion = NuGetVersion.Parse("1.0.0") };

            var result = await Find([Project("App", dependency)], new OutdatedOptions { MaximumVersion = NuGetVersion.Parse("1.5.0") });

            var outdated = Assert.Single(Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies);
            Assert.Equal(LatestVersionUnknownReason.NoLowerBound, outdated.LatestVersionUnknownReason);
            await _resolution.DidNotReceiveWithAnyArgs().ResolveLatestVersionAsync(default!, default);
        }

        [Fact]
        public async Task ALookupThatGivesNoVersion_IsReportedAsNotProvided()
        {
            _resolution.ResolveLatestVersionAsync(Arg.Any<LatestVersionQuery>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<NuGetVersion>(null!));

            var result = await Find([Project("App", Dependency("Package", "1.0.0"))]);

            var outdated = Assert.Single(Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies);
            Assert.Null(outdated.LatestVersion);
            Assert.Equal(LatestVersionUnknownReason.NotProvided, outdated.LatestVersionUnknownReason);
        }

        [Fact]
        public void AKnownLatestVersion_HasNoUnknownReason()
        {
            var outdated = new OutdatedDependency
            {
                Dependency = new Dependency { Name = "Package", VersionRange = VersionRange.Parse("(, 2.0.0)"), ResolvedVersion = NuGetVersion.Parse("1.0.0") },
                LatestVersion = NuGetVersion.Parse("1.5.0"),
            };

            Assert.Equal(LatestVersionUnknownReason.None, outdated.LatestVersionUnknownReason);
        }

        [Fact]
        public async Task ACancelledRequest_StartsNoLookups()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Find([Project("App", Dependency("Package", "1.0.0"))], cancellationToken: cancellation.Token));

            await _resolution.DidNotReceiveWithAnyArgs().ResolveLatestVersionAsync(default!, default);
        }

        [Fact]
        public async Task LooksUpAtMostOneProjectPerProcessorAtATime()
        {
            var inFlight = 0;
            var maxInFlight = 0;
            var resolution = Substitute.For<INuGetPackageResolutionService>();
            resolution.ResolveLatestVersionAsync(Arg.Any<LatestVersionQuery>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var current = Interlocked.Increment(ref inFlight);
                    InterlockedMax(ref maxInFlight, current);
                    await Task.Delay(20);
                    Interlocked.Decrement(ref inFlight);
                    return call.Arg<LatestVersionQuery>().CurrentVersion;
                });

            // One dependency per project, so lookups in flight are projects in flight.
            var projects = Enumerable.Range(0, (Environment.ProcessorCount * 2) + 1)
                .Select(index => Project($"App{index}", Dependency($"Package{index}", "1.0.0")))
                .ToList();

            await Find(projects, resolution: resolution);

            Assert.InRange(maxInFlight, 1, Environment.ProcessorCount);
            await resolution.ReceivedWithAnyArgs(projects.Count).ResolveLatestVersionAsync(default!, default);
        }

        /// <summary>Reports on the caller's thread, unlike <see cref="Progress{T}"/>, so the order is kept.</summary>
        private sealed class SynchronousProgress(Action<OutdatedReportStage> report) : IProgress<OutdatedReportStage>
        {
            public void Report(OutdatedReportStage value) => report(value);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        /// <summary>The projects of the outdated report for a directory holding <paramref name="projects"/>.</summary>
        private async Task<IReadOnlyList<OutdatedProject>> Find(
            IEnumerable<Project> projects,
            OutdatedOptions? options = null,
            INuGetPackageResolutionService? resolution = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var project in projects)
            {
                AddProjectFile(project.FilePath);
                _analyzed[project.FilePath] = [project];
            }

            var report = await Service(resolution).FindOutdatedAsync(
                Root, (options ?? OutdatedOptions.Default) with { Recursive = true }, cancellationToken: cancellationToken);
            return report.Projects;
        }

        // A file-based app is found by its shebang line.
        private void AddProjectFile(string path) =>
            _fileSystem.AddFile(path, new MockFileData(path.EndsWith(".cs", StringComparison.Ordinal) ? "#!/usr/bin/env dotnet" : "<Project />"));

        private OutdatedDependencyService Service(INuGetPackageResolutionService? resolution = null) =>
            new(new ProjectDiscoveryService(_fileSystem), _analysis, resolution ?? _resolution, _fileSystem);

        private static Dependency Dependency(string name, string version, bool isAutoReferenced = false, bool isDevelopmentDependency = false)
        {
            var range = VersionRange.Parse(version);
            return new Dependency { Name = name, VersionRange = range, ResolvedVersion = range.MinVersion, IsAutoReferenced = isAutoReferenced, IsDevelopmentDependency = isDevelopmentDependency };
        }

        private static Project Project(string name, params Dependency[] dependencies) => new()
        {
            Name = name,
            FilePath = XFS.Path($@"c:\src\{name}\{name}.csproj"),
            Sources = [new Uri("https://api.nuget.org/v3/index.json")],
            Version = new NuGetVersion("1.0.0"),
            TargetFrameworks = [new TargetFramework { Name = Net9, TargetAlias = "net9.0", Dependencies = dependencies }],
        };
    }
}
