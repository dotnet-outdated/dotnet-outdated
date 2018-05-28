using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal interface INuGetPackageResolutionService
    {
        Task<(NuGetVersion referencedVersion, NuGetVersion latestVersion)> ResolvePackageVersions(
            string package, List<Uri> sources, VersionRange currentVersionRange, VersionLock versionLock, PrereleaseReporting prerelease);
    }
}