using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface INuGetPackageInfoService
    {
        /// <summary>Lists the versions of a package available from the query's sources.</summary>
        Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(PackageVersionsQuery query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Whether the version declares a .NET target framework unsupported. NuGet treats a package as compatible with a
        /// .NET target framework when one of its netstandard assets applies, and the .NET runtime packages keep that
        /// asset after they drop a .NET version, but add a build warning that the version doesn't support it (such as
        /// "Microsoft.Extensions.Options 11.0.0 doesn't support net8.0 and has not been tested with it"). A version that
        /// reaches the target framework only through netstandard is downloaded, and is unsupported when the MSBuild
        /// files restore would import for that framework, its own or those of the dependencies it brings in, raise
        /// that warning.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> for a version that supports the target framework, and for one that can't be
        /// downloaded or read.
        /// </returns>
        Task<bool> DeclaresTargetFrameworkUnsupportedAsync(TargetFrameworkSupportQuery query, CancellationToken cancellationToken = default);
    }
}
