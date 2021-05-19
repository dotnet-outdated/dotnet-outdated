using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    using System.Collections.Concurrent;
    using System.Linq;

    public class NuGetPackageResolutionService : INuGetPackageResolutionService
    {
        private readonly INuGetPackageInfoService _nugetService;
        private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>> _cache = new ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>>();

        public NuGetPackageResolutionService(INuGetPackageInfoService nugetService)
        {
            _nugetService = nugetService;
        }

        public async Task<NuGetVersion> ResolvePackageVersions(string packageName, NuGetVersion referencedVersion, IEnumerable<Uri> sources, VersionRange currentVersionRange,
            VersionLock versionLock, PrereleaseReporting prerelease, NuGetFramework targetFrameworkName, string projectFilePath, bool isDevelopmentDependency)
        {
            return await ResolvePackageVersions(packageName, referencedVersion, sources, currentVersionRange, versionLock, prerelease, targetFrameworkName, projectFilePath,
                isDevelopmentDependency, 0);
        }

        public async Task<NuGetVersion> ResolvePackageVersions(string packageName, NuGetVersion referencedVersion, IEnumerable<Uri> sources, VersionRange currentVersionRange,
            VersionLock versionLock, PrereleaseReporting prerelease, NuGetFramework targetFrameworkName, string projectFilePath, bool isDevelopmentDependency, int olderThanDays)
        {
            // Determine whether we are interested in pre-releases
            bool includePrerelease = referencedVersion.IsPrerelease;
            if (prerelease == PrereleaseReporting.Always)
                includePrerelease = true;
            else if (prerelease == PrereleaseReporting.Never)
                includePrerelease = false;

            string cacheKey = (packageName + "-" + includePrerelease + "-" + targetFrameworkName + "-" + olderThanDays).ToLowerInvariant();

            // Get all the available versions
            var allVersionsRequest = new Lazy<Task<IReadOnlyList<NuGetVersion>>>(() => this._nugetService.GetAllVersions(packageName, sources, includePrerelease, targetFrameworkName, projectFilePath, isDevelopmentDependency, olderThanDays));
            var allVersions = await this._cache.GetOrAdd(cacheKey, allVersionsRequest).Value;

            // Determine the floating behaviour
            var floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.AbsoluteLatest : NuGetVersionFloatBehavior.Major;
            if (versionLock == VersionLock.Major)
                floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.PrereleaseMinor : NuGetVersionFloatBehavior.Minor;
            if (versionLock == VersionLock.Minor)
                floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.PrereleasePatch : NuGetVersionFloatBehavior.Patch;

            string releasePrefix = null;
            if (referencedVersion.IsPrerelease)
                releasePrefix = referencedVersion.ReleaseLabels.First(); // TODO Not sure exactly what to do for this bit

            // Create a new version range for comparison
            var latestVersionRange = new VersionRange(currentVersionRange, new FloatRange(floatingBehaviour, referencedVersion, releasePrefix));

            // Use new version range to determine latest version
            NuGetVersion latestVersion = latestVersionRange.FindBestMatch(allVersions);

            return latestVersion;
        }
    }
}