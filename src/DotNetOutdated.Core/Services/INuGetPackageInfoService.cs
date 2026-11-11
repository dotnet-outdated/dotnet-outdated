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
    }
}
