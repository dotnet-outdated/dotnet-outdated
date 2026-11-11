#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class OutdatedDependencyServiceTests
    {
        private static readonly NuGetFramework Net9 = NuGetFramework.Parse("net9.0");

        private readonly INuGetPackageResolutionService _resolution = Substitute.For<INuGetPackageResolutionService>();
        private readonly Dictionary<string, string> _latestVersions = new(StringComparer.OrdinalIgnoreCase);

        public OutdatedDependencyServiceTests()
        {
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

            var result = await Service().FindOutdatedAsync([project]);

            var dependencies = Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies;
            Assert.Equal(["Alpha", "Zeta"], dependencies.Select(d => d.Dependency.Name));
            Assert.Equal(DependencyUpgradeSeverity.Minor, dependencies[0].UpgradeSeverity);
            Assert.Equal(DependencyUpgradeSeverity.Major, dependencies[1].UpgradeSeverity);
        }

        [Fact]
        public async Task LeavesOutProjectsWithNothingToReport()
        {
            var result = await Service().FindOutdatedAsync([Project("App", Dependency("Current", "1.0.0"))]);

            Assert.Empty(result);
        }

        [Fact]
        public async Task IncludeUpToDate_ReportsEveryDependency()
        {
            var result = await Service().FindOutdatedAsync(
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

            var result = await Service().FindOutdatedAsync([project], new OutdatedOptions { IncludeAutoReferences = includeAutoReferences });

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

            var result = await Service().FindOutdatedAsync([project], new OutdatedOptions
            {
                IncludeFilters = ["contoso"],
                ExcludeFilters = ["TESTS"],
            });

            Assert.Equal(["Contoso.Core"], result.Single().TargetFrameworks.Single().Dependencies.Select(d => d.Dependency.Name));
        }

        [Fact]
        public async Task MaximumVersion_CapsTheAllowedRangeWithoutWideningIt()
        {
            _latestVersions["Open"] = "2.0.0";
            _latestVersions["Bounded"] = "2.0.0";
            var project = Project("App", Dependency("Open", "1.0.0"), Dependency("Bounded", "[1.0.0, 1.5.0)"));

            await Service().FindOutdatedAsync([project], new OutdatedOptions { MaximumVersion = NuGetVersion.Parse("3.0.0") });

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

            await Service().FindOutdatedAsync([project], new OutdatedOptions
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
                    q.TargetFramework == Net9 && q.ProjectFilePath == "/src/App/App.csproj" && q.IsDevelopmentDependency &&
                    q.VersionLock == VersionLock.Minor && q.Prerelease == PrereleaseReporting.Always && q.PrereleaseLabel == "beta" &&
                    q.OlderThanDays == 7 && q.IgnoreFailedSources),
                Arg.Any<CancellationToken>());
        }

        [Theory]
        [InlineData(null, "1.0.0")]
        [InlineData("1.0.0", "(, 2.0.0)")]
        public async Task UnresolvedOrUnboundedDependencies_AreReportedWithAnUnknownLatestVersion(string? resolvedVersion, string range)
        {
            var dependency = new Dependency("Package", VersionRange.Parse(range), resolvedVersion == null ? null : NuGetVersion.Parse(resolvedVersion), false, false, false);

            var result = await Service().FindOutdatedAsync([Project("App", dependency)]);

            var outdated = Assert.Single(Assert.Single(Assert.Single(result).TargetFrameworks).Dependencies);
            Assert.Null(outdated.LatestVersion);
            Assert.Equal(DependencyUpgradeSeverity.Unknown, outdated.UpgradeSeverity);
            await _resolution.DidNotReceiveWithAnyArgs().ResolveLatestVersionAsync(default!, default);
        }

        [Fact]
        public async Task ACancelledRequest_StartsNoLookups()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Service().FindOutdatedAsync([Project("App", Dependency("Package", "1.0.0"))], cancellationToken: cancellation.Token));

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

            await new OutdatedDependencyService(resolution).FindOutdatedAsync(projects);

            Assert.InRange(maxInFlight, 1, Environment.ProcessorCount);
            await resolution.ReceivedWithAnyArgs(projects.Count).ResolveLatestVersionAsync(default!, default);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private OutdatedDependencyService Service() => new(_resolution);

        private static Dependency Dependency(string name, string version, bool isAutoReferenced = false, bool isDevelopmentDependency = false)
        {
            var range = VersionRange.Parse(version);
            return new Dependency(name, range, range.MinVersion, isAutoReferenced, isTransitive: false, isDevelopmentDependency);
        }

        private static Project Project(string name, params Dependency[] dependencies)
        {
            var project = new Project(name, $"/src/{name}/{name}.csproj", [new Uri("https://api.nuget.org/v3/index.json")], new NuGetVersion("1.0.0"));
            var targetFramework = new TargetFramework(Net9) { TargetAlias = "net9.0" };
            foreach (var dependency in dependencies)
            {
                targetFramework.Dependencies.Add(dependency.Name, dependency);
            }

            project.TargetFrameworks.Add(targetFramework);
            return project;
        }
    }
}
