using NuGet.Frameworks;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface INuGetPackageResolutionService
    {
        Task<NuGetVersion> ResolvePackageVersions(string packageName,
            NuGetVersion referencedVersion, IEnumerable<Uri> sources, VersionRange currentVersionRange, VersionLock versionLock, PrereleaseReporting prerelease,
            NuGetFramework targetFrameworkName, string projectFilePath, bool isDevelopmentDependency);

        Task<NuGetVersion> ResolvePackageVersions(string packageName,
            NuGetVersion referencedVersion, IEnumerable<Uri> sources, VersionRange currentVersionRange, VersionLock versionLock, PrereleaseReporting prerelease,
            NuGetFramework targetFrameworkName, string projectFilePath, bool isDevelopmentDependency, int olderThanDays, bool ignoreFailedSources);
    }
}
