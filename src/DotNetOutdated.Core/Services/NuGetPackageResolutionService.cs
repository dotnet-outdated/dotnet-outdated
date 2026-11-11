using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public class NuGetPackageResolutionService(INuGetPackageInfoService nugetService) : INuGetPackageResolutionService
    {
        private readonly INuGetPackageInfoService _nugetService = nugetService;
        private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>> _cache = [];

        public async Task<NuGetVersion> ResolveLatestVersionAsync(LatestVersionQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var packageName = query.PackageId;
            var referencedVersion = query.CurrentVersion;
            var currentVersionRange = query.AllowedRange;
            var versionLock = query.VersionLock;
            var prerelease = query.Prerelease;
            var prereleaseLabel = query.PrereleaseLabel;
            var targetFrameworkName = query.TargetFramework;
            var olderThanDays = query.OlderThanDays;

            ArgumentNullException.ThrowIfNull(referencedVersion);
            ArgumentNullException.ThrowIfNull(currentVersionRange);

            if (currentVersionRange.MinVersion is null)
                throw new ArgumentException($"No minimum version specified for package {packageName}.", nameof(query));

            // Determine whether we are interested in pre-releases
            bool includePrerelease = referencedVersion.IsPrerelease;
            if (prerelease == PrereleaseReporting.Always)
                includePrerelease = true;
            else if (prerelease == PrereleaseReporting.Never)
                includePrerelease = false;

            string cacheKey = (packageName + "-" + includePrerelease + "-" + targetFrameworkName + "-" + olderThanDays).ToUpperInvariant();

            // Get all the available versions
            // The request is shared by every caller with the same key, so it doesn't take one caller's cancellation token.
            var allVersionsRequest = new Lazy<Task<IReadOnlyList<NuGetVersion>>>(() => _nugetService.GetAllVersionsAsync(
                new PackageVersionsQuery
                {
                    PackageId = packageName,
                    Sources = query.Sources,
                    TargetFramework = targetFrameworkName,
                    ProjectFilePath = query.ProjectFilePath,
                    IncludePrerelease = includePrerelease,
                    IsDevelopmentDependency = query.IsDevelopmentDependency,
                    OlderThanDays = olderThanDays,
                    IgnoreFailedSources = query.IgnoreFailedSources,
                },
                CancellationToken.None));
            var allVersions = await _cache.GetOrAdd(cacheKey, allVersionsRequest).Value.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Determine the floating behaviour
            var floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.AbsoluteLatest : NuGetVersionFloatBehavior.Major;
            if (versionLock == VersionLock.Major)
                floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.PrereleaseMinor : NuGetVersionFloatBehavior.Minor;
            if (versionLock == VersionLock.Minor)
                floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.PrereleasePatch : NuGetVersionFloatBehavior.Patch;

            string releasePrefix = string.Empty;
            if (referencedVersion.IsPrerelease)
            {
                if (!string.IsNullOrWhiteSpace(prereleaseLabel))
                {
                    releasePrefix = prereleaseLabel;
                }
                else
                {
                    releasePrefix = referencedVersion.ReleaseLabels.First();
                }
            }

            // Create a new version range for comparison
            var latestVersionRange = new VersionRange(currentVersionRange, new FloatRange(floatingBehaviour, referencedVersion, releasePrefix));

            // Use new version range to determine latest version
            NuGetVersion? latestVersion = latestVersionRange.FindBestMatch(allVersions);

            return latestVersion ?? referencedVersion;
        }
    }
}
