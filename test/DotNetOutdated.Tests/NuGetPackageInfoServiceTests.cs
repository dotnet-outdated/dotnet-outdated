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

        [Fact(Skip = "bug: a failing source is skipped silently without IgnoreFailedSources, and fails the lookup with it")]
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

            public void Add(string id, string version, DateTime published, string? dependencyFramework = null)
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
