using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal interface INuGetPackageInfoService
    {
        Task<NuGetVersion> GetLatestVersion(string package, List<Uri> sources, bool includePrerelease);
    }
}