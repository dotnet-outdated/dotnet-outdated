using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace DotNetOutdated.Tests;

/// <summary>
/// A local NuGet feed of fake packages and MSBuild SDKs for the end-to-end tests, so their results don't depend on
/// what is published to nuget.org. Every <c>DotNetOutdated.Fake.*</c> id is mapped to this feed only; nuget.org is
/// still used for the packages the .NET SDK itself needs (such as <c>NETStandard.Library</c>), at the fixed versions
/// the SDK asks for.
/// </summary>
/// <remarks>
/// The feed and its global packages folder are built once per catalog, in a folder named after a hash of the catalog,
/// so changing a package below gets a fresh feed and never a stale extracted copy.
/// </remarks>
internal static class TestPackageFeed
{
    private static readonly NuGetFramework LibraryFramework = NuGetFramework.Parse("netstandard1.0");

    private static readonly string[] ExtensionsVersions = ["2.1.0", "6.0.0", "6.0.1", "7.0.0", "8.0.0", "8.0.1", "9.0.0", "10.0.0"];

    private static readonly IReadOnlyList<FakePackage> Catalog =
    [
        .. Library("DotNetOutdated.Fake.Json", "11.0.1", "12.0.1", "12.0.3", "13.0.1", "13.0.3", "13.0.4"),
        .. Library("DotNetOutdated.Fake.DependencyInjection", ExtensionsVersions),
        .. ExtensionsVersions.Select(version => new FakePackage("DotNetOutdated.Fake.Logging", version)
        {
            Dependencies = [("DotNetOutdated.Fake.DependencyInjection", version)],
        }),
        .. Library("DotNetOutdated.Fake.Options", ExtensionsVersions),
        .. Library("DotNetOutdated.Fake.TextJson", "6.0.0", "6.0.1", "7.0.0", "8.0.0", "8.0.1", "9.0.0", "10.0.0"),
        .. Library("DotNetOutdated.Fake.TextEncodings", "6.0.0", "8.0.0", "9.0.0"),
        .. Library("DotNetOutdated.Fake.AsyncInterfaces", "8.0.0", "9.0.0", "10.0.0"),
        .. Library("DotNetOutdated.Fake.Http", "1.0.4", "2.0.2", "2.3.0"),
        .. DevelopmentDependency("DotNetOutdated.Fake.Coverage", "2.3.0", "6.0.0"),
        .. DevelopmentDependency("DotNetOutdated.Fake.TestAdapter", "2.4.0", "3.0.0"),
        .. DevelopmentDependency("DotNetOutdated.Fake.StyleAnalyzers", "1.0.0", "1.1.0"),
        .. DevelopmentDependency("DotNetOutdated.Fake.QualityAnalyzers", "2.6.0", "3.0.0"),

        // A project SDK that adds nothing to Microsoft.NET.Sdk, like Microsoft.Build.NoTargets.
        .. Sdk("DotNetOutdated.Fake.NoTargets.Sdk", ["3.7.0", "3.8.0"], injectedPackages: []),

        // A project SDK that adds package references of its own, like MSTest.Sdk.
        .. Sdk("DotNetOutdated.Fake.Test.Sdk", ["3.6.0", "3.7.0"], injectedPackages: ["DotNetOutdated.Fake.TestFramework", "DotNetOutdated.Fake.TestHost"]),
        .. Library("DotNetOutdated.Fake.TestFramework", "3.6.0", "3.7.0"),
        .. Library("DotNetOutdated.Fake.TestHost", "3.6.0", "3.7.0"),

        // An SDK for file-based apps that adds a package reference of its own, like Cake.Sdk.
        .. Sdk("DotNetOutdated.Fake.App.Sdk", ["6.0.0", "6.1.0"], injectedPackages: ["DotNetOutdated.Fake.App.Generator"]),
        .. Library("DotNetOutdated.Fake.App.Generator", "6.0.0", "6.1.0"),
    ];

    private static readonly Lazy<(string Feed, string GlobalPackagesFolder)> Paths = new(Build);

    /// <summary>The folder holding the feed's <c>.nupkg</c> files.</summary>
    public static string FeedPath => Paths.Value.Feed;

    /// <summary>
    /// A NuGet.Config for a test project: the fake packages come from this feed only, everything else from
    /// nuget.org, and packages are extracted to a global packages folder of the feed's own.
    /// </summary>
    public static string NuGetConfig =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="{SecurityElement.Escape(Paths.Value.GlobalPackagesFolder)}" />
          </config>
          <packageSources>
            <clear />
            <add key="fake" value="{SecurityElement.Escape(Paths.Value.Feed)}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="fake">
              <package pattern="DotNetOutdated.Fake.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    /// <summary>The <c>contentHash</c> NuGet records in a lock file for a package of the feed.</summary>
    public static string ContentHash(string id, string version)
    {
        var path = Path.Combine(FeedPath, $"{id}.{NuGetVersion.Parse(version).ToNormalizedString()}.nupkg".ToLowerInvariant());
        return Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(path)));
    }

    private static IEnumerable<FakePackage> Library(string id, params string[] versions) =>
        versions.Select(version => new FakePackage(id, version));

    private static IEnumerable<FakePackage> DevelopmentDependency(string id, params string[] versions) =>
        versions.Select(version => new FakePackage(id, version) { IsDevelopmentDependency = true });

    private static IEnumerable<FakePackage> Sdk(string id, string[] versions, string[] injectedPackages) =>
        versions.Select(version => new FakePackage(id, version)
        {
            IsSdk = true,
            InjectedPackages = [.. injectedPackages.Select(package => (package, version))],
        });

    private static (string Feed, string GlobalPackagesFolder) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-outdated-test-feed", CatalogHash());
        var feed = Path.Combine(root, "feed");
        var globalPackagesFolder = Path.Combine(root, "packages");

        if (!Directory.Exists(feed))
        {
            // Build next to the final folder and move it into place, so a concurrent test run (another target
            // framework) never sees a partial feed.
            var staging = Path.Combine(root, $"feed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            foreach (var package in Catalog)
            {
                package.Save(staging);
            }

            try
            {
                Directory.Move(staging, feed);
            }
            catch (IOException) when (Directory.Exists(feed))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        Directory.CreateDirectory(globalPackagesFolder);
        return (feed, globalPackagesFolder);
    }

    private static string CatalogHash()
    {
        var description = string.Join("\n", Catalog.Select(package => package.Describe()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(description));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record FakePackage(string Id, string Version)
    {
        public bool IsDevelopmentDependency { get; init; }

        public bool IsSdk { get; init; }

        public (string Id, string Version)[] Dependencies { get; init; } = [];

        public (string Id, string Version)[] InjectedPackages { get; init; } = [];

        public string Describe() =>
            $"{Id}/{Version} dev={IsDevelopmentDependency} sdk={IsSdk} " +
            $"deps={string.Join(",", Dependencies)} injected={string.Join(",", InjectedPackages)}";

        public void Save(string folder)
        {
            var builder = new PackageBuilder(deterministic: true)
            {
                Id = Id,
                Version = NuGetVersion.Parse(Version),
                Description = "A fake package for the dotnet-outdated end-to-end tests.",
                DevelopmentDependency = IsDevelopmentDependency,
            };
            builder.Authors.Add("dotnet-outdated");

            if (IsSdk)
            {
                builder.PackageTypes.Add(new PackageType("MSBuildSdk", PackageType.EmptyVersion));
                builder.Files.Add(TextFile("Sdk/Sdk.props", SdkProps()));
                builder.Files.Add(TextFile("Sdk/Sdk.targets", """
                    <Project>
                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
                    </Project>
                    """));
            }
            else
            {
                builder.Files.Add(TextFile($"lib/{LibraryFramework.GetShortFolderName()}/_._", string.Empty));
            }

            if (Dependencies.Length > 0)
            {
                builder.DependencyGroups.Add(new PackageDependencyGroup(
                    LibraryFramework,
                    Dependencies.Select(dependency => new PackageDependency(dependency.Id, VersionRange.Parse(dependency.Version)))));
            }

            var path = Path.Combine(folder, $"{Id}.{builder.Version.ToNormalizedString()}.nupkg".ToLowerInvariant());
            using var stream = File.Create(path);
            builder.Save(stream);
        }

        private string SdkProps()
        {
            var references = string.Concat(InjectedPackages.Select(package =>
                $"""

                    <PackageReference Include="{package.Id}" Version="{package.Version}" />
                """));

            return $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <ItemGroup>{references}
                  </ItemGroup>
                </Project>
                """;
        }

        private static PhysicalPackageFile TextFile(string targetPath, string content) =>
            new(new MemoryStream(Encoding.UTF8.GetBytes(content))) { TargetPath = targetPath };
    }
}
