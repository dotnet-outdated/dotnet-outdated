using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface INuGetPackageResolutionService
    {
        /// <summary>
        /// Finds the newest version that <see cref="LatestVersionQuery.CurrentVersion"/> may move to.
        /// </summary>
        /// <returns>The newest allowed version, or the current version when there is no newer one.</returns>
        Task<NuGetVersion> ResolveLatestVersionAsync(LatestVersionQuery query, CancellationToken cancellationToken = default);
    }
}
