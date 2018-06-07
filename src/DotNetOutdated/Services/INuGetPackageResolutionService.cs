using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGet.Packaging.Core;
using NuGet.Frameworks;

namespace DotNetOutdated.Services
{
    internal interface INuGetPackageResolutionService
    {
        Task<(NuGetVersion referencedVersion, NuGetVersion latestVersion)> ResolvePackageVersions(
            string package, List<Uri> sources, VersionRange currentVersionRange, VersionLock versionLock, PrereleaseReporting prerelease);
        Task<IEnumerable<PackageDependency>> GetDependencies(NuGetVersion referencedVersion,
            string package, List<Uri> sources, NuGetFramework targetFramework);
    }
}