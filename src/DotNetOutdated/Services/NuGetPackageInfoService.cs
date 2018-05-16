using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    public class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        private SourceCacheContext _context;
        private FindPackageByIdResource _findPackageById;
        private NullLogger _logger;

        private async Task<FindPackageByIdResource> GetFindPackageByIdResource()
        {
            if (_findPackageById == null)
            {
                _logger = new NullLogger();
                _context = new SourceCacheContext()
                {
                    NoCache = true
                };
                
                var sourceUrl = "https://api.nuget.org/v3/index.json";
                var sourceRepository = Repository.Factory.GetCoreV3(sourceUrl);
                _findPackageById = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
            }

            return _findPackageById;
        }
        
        public async Task<NuGetVersion> GetLatestVersion(string package, bool includePrerelease)
        {
            var findPackageById = await GetFindPackageByIdResource();

            return (await findPackageById.GetAllVersionsAsync(package, _context, _logger, CancellationToken.None))
                .OrderByDescending(version => version)
                .FirstOrDefault();
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
    
}