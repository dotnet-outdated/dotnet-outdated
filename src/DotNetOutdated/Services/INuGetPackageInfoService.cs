using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal interface INuGetPackageInfoService
    {
        Task<IEnumerable<NuGetVersion>> GetAllVersions(string package, List<Uri> sources, bool includePrerelease, NuGetFramework targetFramework, string projectFilePath);
    }
}