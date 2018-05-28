using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal class NuGetPackageResolutionService : INuGetPackageResolutionService
    {
        private readonly INuGetPackageInfoService _nugetService;

        public NuGetPackageResolutionService(INuGetPackageInfoService nugetService)
        {
            _nugetService = nugetService;
        }
        
        public async Task<(NuGetVersion referencedVersion, NuGetVersion latestVersion)> ResolvePackageVersions(
            string package, List<Uri> sources, VersionRange currentVersionRange, VersionLock versionLock, PrereleaseReporting prerelease)
        {
            // Get all the available versions
            var allVersions = await _nugetService.GetAllVersions(package, sources);

            // Resolve the referenced versions
            NuGetVersion referencedVersion = currentVersionRange.FindBestMatch(allVersions);

            // Determine whether we are interested in pre-releases
            bool includePrerelease = referencedVersion.IsPrerelease;
            if (prerelease == PrereleaseReporting.Always)
                includePrerelease = true;
            else if (prerelease == PrereleaseReporting.Never)
                includePrerelease = false;

            // Determine the floating behaviour
            var floatingBehaviour = includePrerelease ? NuGetVersionFloatBehavior.AbsoluteLatest : NuGetVersionFloatBehavior.Major;
            if (versionLock == VersionLock.Major)
                floatingBehaviour = NuGetVersionFloatBehavior.Minor;
            if (versionLock == VersionLock.Minor)
                floatingBehaviour = NuGetVersionFloatBehavior.Patch;
            
            // Create a new version range for comparison
            var latestVersionRange = new VersionRange(currentVersionRange, new FloatRange(floatingBehaviour, referencedVersion));
            
            // Use new version range to determine latest version
            NuGetVersion latestVersion = latestVersionRange.FindBestMatch(allVersions);

            return (referencedVersion, latestVersion);
        }
    }
}