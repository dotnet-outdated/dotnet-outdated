#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class NuGetPackageInfoServiceTests
    {
        [Fact]
        public async Task GetAllVersionsAsync_StopsWaitingForAStalledFeedWhenCancelled()
        {
            // A feed that accepts connections but never answers, so service index discovery doesn't complete.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var service = new NuGetPackageInfoService();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            var lookup = service.GetAllVersionsAsync(
                new PackageVersionsQuery
                {
                    PackageId = "Contoso.Widgets",
                    Sources = [new Uri($"http://127.0.0.1:{port}/v3/index.json")],
                    TargetFramework = NuGetFramework.Parse("net9.0"),
                },
                cancellation.Token);

            var finished = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(lookup, finished);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        }

        [Theory]
        [InlineData(0, new[] { "1.0.0", "2.0.0", "3.0.0" })]
        [InlineData(10, new[] { "1.0.0", "2.0.0" })]
        [InlineData(100, new[] { "1.0.0" })]
        [InlineData(1000, new string[0])]
        public async Task GetAllVersionsAsync_LeavesOutVersionsPublishedWithinOlderThanDays(int olderThanDays, string[] expected)
        {
            using var feed = new LocalFeed();
            // A local feed reports a package's file time as its publish date.
            feed.Add("Contoso.Widgets", "1.0.0", published: DateTime.UtcNow.AddDays(-200));
            feed.Add("Contoso.Widgets", "2.0.0", published: DateTime.UtcNow.AddDays(-50));
            feed.Add("Contoso.Widgets", "3.0.0", published: DateTime.UtcNow.AddDays(-1));
            using var service = new NuGetPackageInfoService();

            var versions = await service.GetAllVersionsAsync(new PackageVersionsQuery
            {
                PackageId = "Contoso.Widgets",
                Sources = [feed.Uri],
                TargetFramework = NuGetFramework.Parse("net9.0"),
                OlderThanDays = olderThanDays,
            });

            Assert.Equal(expected, versions.Select(v => v.ToNormalizedString()).Order(StringComparer.Ordinal));
        }

        [Theory]
        [InlineData(false, new[] { "1.0.0", "2.0.0" })]
        [InlineData(true, new[] { "1.0.0", "2.0.0", "3.0.0" })]
        public async Task GetAllVersionsAsync_LeavesOutVersionsForOtherFrameworksUnlessADevelopmentDependency(bool isDevelopmentDependency, string[] expected)
        {
            using var feed = new LocalFeed();
            feed.Add("Contoso.Widgets", "1.0.0", published: DateTime.UtcNow.AddDays(-3));
            feed.Add("Contoso.Widgets", "2.0.0", published: DateTime.UtcNow.AddDays(-2), dependencyFramework: "netstandard2.0");
            feed.Add("Contoso.Widgets", "3.0.0", published: DateTime.UtcNow.AddDays(-1), dependencyFramework: "net48");
            using var service = new NuGetPackageInfoService();

            var versions = await service.GetAllVersionsAsync(new PackageVersionsQuery
            {
                PackageId = "Contoso.Widgets",
                Sources = [feed.Uri],
                TargetFramework = NuGetFramework.Parse("net9.0"),
                IsDevelopmentDependency = isDevelopmentDependency,
            });

            Assert.Equal(expected, versions.Select(v => v.ToNormalizedString()).Order(StringComparer.Ordinal));
        }

        [Theory]
        [InlineData("Contoso.Widgets", new[] { "2.0.0" })]
        [InlineData("Fabrikam.Gadgets", new[] { "1.0.0" })]
        [InlineData("Unmapped.Package", new string[0])]
        public async Task GetAllVersionsAsync_QueriesOnlyTheSourcesMappedToThePackage(string packageId, string[] expected)
        {
            using var project = new LocalFeed();
            using var feedA = new LocalFeed();
            using var feedB = new LocalFeed();
            foreach (var id in new[] { "Contoso.Widgets", "Fabrikam.Gadgets", "Unmapped.Package" })
            {
                feedA.Add(id, "1.0.0", published: DateTime.UtcNow.AddDays(-1));
                feedB.Add(id, "2.0.0", published: DateTime.UtcNow.AddDays(-1));
            }

            File.WriteAllText(System.IO.Path.Combine(project.Path, "NuGet.Config"), $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="a" value="{{System.Security.SecurityElement.Escape(feedA.Path)}}" />
                    <add key="b" value="{{System.Security.SecurityElement.Escape(feedB.Path)}}" />
                  </packageSources>
                  <packageSourceMapping>
                    <packageSource key="A">
                      <package pattern="Fabrikam.*" />
                    </packageSource>
                    <packageSource key="b">
                      <package pattern="Contoso.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);
            using var service = new NuGetPackageInfoService();

            var versions = await service.GetAllVersionsAsync(new PackageVersionsQuery
            {
                PackageId = packageId,
                Sources = [new Uri(feedA.Path), new Uri(feedB.Path)],
                TargetFramework = NuGetFramework.Parse("net9.0"),
                ProjectFilePath = System.IO.Path.Combine(project.Path, "App.csproj"),
            });

            Assert.Equal(expected, versions.Select(v => v.ToNormalizedString()).Order(StringComparer.Ordinal));
        }

        [Fact]
        public async Task GetAllVersionsAsync_FailsForABrokenSourceUnlessFailedSourcesAreIgnored()
        {
            using var broken = new LocalFeed();
            File.WriteAllText(System.IO.Path.Combine(broken.Path, "contoso.widgets.2.0.0.nupkg"), "not a zip file");
            using var working = new LocalFeed();
            working.Add("Contoso.Widgets", "1.0.0", published: DateTime.UtcNow.AddDays(-1));
            using var service = new NuGetPackageInfoService();
            PackageVersionsQuery Query(bool ignoreFailedSources) => new()
            {
                PackageId = "Contoso.Widgets",
                Sources = [broken.Uri, working.Uri],
                TargetFramework = NuGetFramework.Parse("net9.0"),
                IgnoreFailedSources = ignoreFailedSources,
            };

            // Like `dotnet restore --ignore-failed-sources`: the broken source is skipped and the others still answer.
            var versions = await service.GetAllVersionsAsync(Query(ignoreFailedSources: true));
            Assert.Equal(["1.0.0"], versions.Select(v => v.ToNormalizedString()));

            // Without it, the failure is reported rather than giving an incomplete list of versions.
            await Assert.ThrowsAnyAsync<Exception>(() => service.GetAllVersionsAsync(Query(ignoreFailedSources: false)));
        }

        // Modeled on the .NET runtime packages (Microsoft.Extensions.*): a netstandard fallback for older frameworks,
        // with a target that warns "doesn't support netX.0" for the .NET versions the package no longer supports.
        private const string CompatError = """
            <Project InitialTargets="NETStandardCompatError_Contoso_Widgets_net10_0">
              <Target Name="NETStandardCompatError_Contoso_Widgets_net10_0" Condition="'$(SuppressTfmSupportBuildWarnings)' == ''">
                <Warning Text="Contoso.Widgets doesn't support $(TargetFramework) and has not been tested with it." />
              </Target>
            </Project>
            """;

        private const string OtherTargets = "<Project />";

        [Theory]
        // Supports net8.0 itself (and carries the warning for the frameworks below it).
        [InlineData("1.0.0", "net8.0", false)]
        // Dropped net8.0: reaches it through netstandard2.0, where the netcoreapp2.0 target warns.
        [InlineData("2.0.0", "net8.0", true)]
        [InlineData("2.0.0", "net10.0", false)]
        [InlineData("2.0.0", "netstandard2.0", false)]
        // Reaches net8.0 through netstandard2.0 too, but doesn't warn.
        [InlineData("3.0.0", "net8.0", false)]
        // Reaches net8.0 through netstandard2.0 for its dependencies, but has MSBuild files for net8.0, which restore picks.
        [InlineData("4.0.0", "net8.0", false)]
        // Warns from build rather than buildTransitive, which a direct reference imports too.
        [InlineData("5.0.0", "net8.0", true)]
        // Not on the feed.
        [InlineData("9.0.0", "net8.0", false)]
        public async Task DeclaresTargetFrameworkUnsupportedAsync_FindsTheRuntimePackagesWarning(string version, string targetFramework, bool expected)
        {
            using var feed = new LocalFeed();
            feed.Add("Contoso.Widgets", "1.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net8.0", "netstandard2.0"],
                files: [("buildTransitive/net8.0/Contoso.Widgets.targets", OtherTargets), ("buildTransitive/netcoreapp2.0/Contoso.Widgets.targets", CompatError)]);
            feed.Add("Contoso.Widgets", "2.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                files: [("buildTransitive/net10.0/Contoso.Widgets.targets", OtherTargets), ("buildTransitive/netcoreapp2.0/Contoso.Widgets.targets", CompatError)]);
            feed.Add("Contoso.Widgets", "3.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net11.0", "netstandard2.0"]);
            feed.Add("Contoso.Widgets", "4.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                files: [("buildTransitive/net8.0/Contoso.Widgets.targets", OtherTargets), ("buildTransitive/netcoreapp2.0/Contoso.Widgets.targets", CompatError)]);
            feed.Add("Contoso.Widgets", "5.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                files: [("build/netcoreapp2.0/Contoso.Widgets.targets", CompatError)]);
            using var service = new NuGetPackageInfoService();

            var unsupported = await service.DeclaresTargetFrameworkUnsupportedAsync(SupportQuery(feed, "Contoso.Widgets", version, targetFramework));

            Assert.Equal(expected, unsupported);
        }

        [Theory]
        // Like Microsoft.Extensions.Diagnostics.HealthChecks 11.0.0: no targets of its own, but its netstandard2.0
        // dependencies warn that net8.0 is not supported.
        [InlineData("Contoso.Widgets", "2.0.0", true)]
        [InlineData("Contoso.Widgets", "1.0.0", false)]
        // A dependency imports only buildTransitive, so a warning under build doesn't reach the project.
        [InlineData("Contoso.Gizmos", "1.0.0", false)]
        public async Task DeclaresTargetFrameworkUnsupportedAsync_ChecksTheDependenciesForTheTargetFramework(string dependencyId, string dependencyVersion, bool expected)
        {
            using var feed = new LocalFeed();
            feed.Add("Contoso.Widgets", "1.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net8.0", "netstandard2.0"]);
            feed.Add("Contoso.Widgets", "2.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                files: [("buildTransitive/netcoreapp2.0/Contoso.Widgets.targets", CompatError)]);
            feed.Add("Contoso.Gizmos", "1.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                files: [("build/netcoreapp2.0/Contoso.Gizmos.targets", CompatError)]);
            feed.Add("Contoso.Gadgets", "1.0.0", published: DateTime.UtcNow, dependencyFrameworks: ["net10.0", "netstandard2.0"],
                dependencies: [(dependencyId, dependencyVersion)]);
            using var service = new NuGetPackageInfoService();

            var unsupported = await service.DeclaresTargetFrameworkUnsupportedAsync(SupportQuery(feed, "Contoso.Gadgets", "1.0.0", "net8.0"));

            Assert.Equal(expected, unsupported);
        }

        private static TargetFrameworkSupportQuery SupportQuery(LocalFeed feed, string packageId, string version, string targetFramework) => new()
        {
            PackageId = packageId,
            Version = NuGetVersion.Parse(version),
            TargetFramework = NuGetFramework.Parse(targetFramework),
            Sources = [feed.Uri],
        };

        /// <summary>A folder of <c>.nupkg</c> files used as a package source.</summary>
        private sealed class LocalFeed : IDisposable
        {
            public LocalFeed()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dotnet-outdated-feed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public Uri Uri => new(Path + System.IO.Path.DirectorySeparatorChar);

            public void Add(string id, string version, DateTime published, string? dependencyFramework = null,
                string[]? dependencyFrameworks = null, (string Path, string Content)[]? files = null,
                (string Id, string Version)[]? dependencies = null)
            {
                var builder = new PackageBuilder(deterministic: true)
                {
                    Id = id,
                    Version = NuGetVersion.Parse(version),
                    Description = "A fake package.",
                };
                builder.Authors.Add("dotnet-outdated");
                builder.Files.Add(new PhysicalPackageFile(new MemoryStream([])) { TargetPath = "lib/netstandard2.0/_._" });
                if (dependencyFramework != null)
                {
                    builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.Parse(dependencyFramework), []));
                }

                foreach (var framework in dependencyFrameworks ?? [])
                {
                    builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.Parse(framework),
                        (dependencies ?? []).Select(d => new NuGet.Packaging.Core.PackageDependency(d.Id, VersionRange.Parse(d.Version)))));
                }

                foreach (var (filePath, content) in files ?? [])
                {
                    builder.Files.Add(new PhysicalPackageFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))) { TargetPath = filePath });
                }

                var path = System.IO.Path.Combine(Path, $"{id}.{version}.nupkg".ToLowerInvariant());
                using (var stream = File.Create(path))
                {
                    builder.Save(stream);
                }

                File.SetLastWriteTimeUtc(path, published);
            }

            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
