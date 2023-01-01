using NuGet.Frameworks;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface INuGetPackageInfoService
    {
        Task<IReadOnlyList<NuGetVersion>> GetAllVersions(string package, IEnumerable<Uri> sources, bool includePrerelease, NuGetFramework targetFramework, string projectFilePath,
            bool isDevelopmentDependency);

        Task<IReadOnlyList<NuGetVersion>> GetAllVersions(string package, IEnumerable<Uri> sources, bool includePrerelease, NuGetFramework targetFramework, string projectFilePath,
            bool isDevelopmentDependency, int olderThanDays, bool ignoreFailedSources);
    }
}
