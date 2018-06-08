using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        private readonly SourceCacheContext _context;
        private readonly NullLogger _logger;
        private readonly Dictionary<string, FindPackageByIdResource> _resources = new Dictionary<string, FindPackageByIdResource>();

        public NuGetPackageInfoService()
        {
            _logger = new NullLogger();
            _context = new SourceCacheContext()
            {
                NoCache = true
            };
        }

        private async Task<FindPackageByIdResource> FindResourceForSource(Uri source)
        {
            string resourceUrl = source.AbsoluteUri;

            var resource = _resources.GetValueOrDefault(resourceUrl);
            if (resource == null)
            {
                var sourceRepository = Repository.Factory.GetCoreV3(resourceUrl);
                resource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();

                _resources.Add(resourceUrl, resource);
            }

            return resource;
        }

        public async Task<IEnumerable<NuGetVersion>> GetAllVersions(string package, List<Uri> sources)
        {
            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                var findPackageById = await FindResourceForSource(source);

                allVersions.AddRange(await findPackageById.GetAllVersionsAsync(package, _context, _logger, CancellationToken.None));
            }

            return allVersions;
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }

}