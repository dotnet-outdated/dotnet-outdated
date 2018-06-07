using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGet.Packaging.Core;
using NuGet.Frameworks;

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

        public async Task<IEnumerable<PackageDependency>> GetDependencies(string package, List<Uri> sources, NuGetVersion referencedVersion, NuGetFramework targetFramework)
        {
            var allDependencies = new List<PackageDependency>();
            foreach (var source in sources)
            {
                var findPackageById = await FindResourceForSource(source);
                var dependencyInfo = await findPackageById.GetDependencyInfoAsync(package, referencedVersion, _context, _logger, CancellationToken.None);

                var reducer = new FrameworkReducer();
                var comparer = new NuGetFrameworkFullComparer();

                if (dependencyInfo != null)
                {
                    var nearestFramework = reducer.GetNearest(targetFramework, dependencyInfo.DependencyGroups.Select(x => x.TargetFramework));

                    foreach (var dependencyGroup in dependencyInfo.DependencyGroups.Where(d => comparer.Equals(nearestFramework, d.TargetFramework)))
                    {                       
                        foreach (var groupPackage in dependencyGroup.Packages)
                        {
                            allDependencies.Add(groupPackage);
                        }
                    }

                }
            }

            return allDependencies;
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }

}