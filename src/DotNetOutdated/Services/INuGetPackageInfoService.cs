using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGet.Packaging.Core;
using NuGet.Frameworks;

namespace DotNetOutdated.Services
{
    internal interface INuGetPackageInfoService
    {
        Task<IEnumerable<NuGetVersion>> GetAllVersions(string package, List<Uri> sources);
        Task<IEnumerable<PackageDependency>> GetDependencies(string package, List<Uri> sources, NuGetVersion referencedVersion, NuGetFramework targetFramework);
    }
}