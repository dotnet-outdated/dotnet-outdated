using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

public class NuGetPackageResolutionService(INuGetPackageInfoService nugetService) : INuGetPackageResolutionService
{
    private readonly INuGetPackageInfoService _nugetService = nugetService;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>> _cache = [];

    public async Task<NuGetVersion> ResolvePackageVersions(
        string packageName,
        NuGetVersion referencedVersion,
        IEnumerable<Uri> sources,
        VersionRange currentVersionRange,
        VersionLock versionLock,
        PrereleaseReporting prerelease,
        string prereleaseLabel,
        NuGetFramework targetFrameworkName,
        string projectFilePath,
        bool isDevelopmentDependency)
    {
        return await ResolvePackageVersions(
            packageName,
            referencedVersion,
            sources,
            currentVersionRange,
            versionLock,
            prerelease,
            prereleaseLabel,
            targetFrameworkName,
            projectFilePath,
            isDevelopmentDependency,
            0).ConfigureAwait(false);
    }

    public async Task<NuGetVersion> ResolvePackageVersions(
        string packageName,
        NuGetVersion referencedVersion,
        IEnumerable<Uri> sources,
        VersionRange currentVersionRange,
        VersionLock versionLock,
        PrereleaseReporting prerelease,
        string prereleaseLabel,
        NuGetFramework targetFrameworkName,
        string projectFilePath,
        bool isDevelopmentDependency,
        int olderThanDays,
        bool ignoreFailedSources = false)
    {
        ArgumentNullException.ThrowIfNull(referencedVersion);
        ArgumentNullException.ThrowIfNull(currentVersionRange);

        if (currentVersionRange.MinVersion is null)
            throw new ArgumentException($"No minimum version specified for package {packageName}.", nameof(currentVersionRange));

        // Determine whether we are interested in pre-releases
        bool includePrerelease = referencedVersion.IsPrerelease;
        if (prerelease == PrereleaseReporting.Always)
            includePrerelease = true;
        else if (prerelease == PrereleaseReporting.Never)
            includePrerelease = false;

        string cacheKey = (packageName + "-" + includePrerelease + "-" + targetFrameworkName + "-" + olderThanDays).ToUpperInvariant();

        // Get all the available versions
        var allVersionsRequest = new Lazy<Task<IReadOnlyList<NuGetVersion>>>(() => this._nugetService.GetAllVersions(packageName, sources, includePrerelease, targetFrameworkName, projectFilePath, isDevelopmentDependency, olderThanDays, ignoreFailedSources));
        var allVersions = await _cache.GetOrAdd(cacheKey, allVersionsRequest).Value.ConfigureAwait(false);

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
        NuGetVersion latestVersion = latestVersionRange.FindBestMatch(allVersions);

        return latestVersion ?? referencedVersion;
    }
}