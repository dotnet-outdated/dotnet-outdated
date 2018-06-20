using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        private readonly SourceCacheContext _context;
        private readonly Dictionary<string, PackageMetadataResource> _metadataResources = new Dictionary<string, PackageMetadataResource>();
        private readonly IEnumerable<PackageSource> _enabledSources;

        public NuGetPackageInfoService()
        {
            var settings = Settings.LoadDefaultSettings(null);
            _enabledSources = SettingsUtility.GetEnabledSources(settings);

            _context = new SourceCacheContext()
            {
                NoCache = true
            };
        }

        private async Task<PackageMetadataResource> FindMetadataResourceForSource(Uri source)
        {
            string resourceUrl = source.AbsoluteUri;

            var resource = _metadataResources.GetValueOrDefault(resourceUrl);
            if (resource == null)
            {
                // We try and create the source repository from the enable sources we loaded from config.
                // This allows us to inherit the username/password for the source from the config and thus
                // enables secure feeds to work properly
                var enabledSource = _enabledSources?.FirstOrDefault(s => s.SourceUri == source);
                var sourceRepository = enabledSource != null ? 
                    new SourceRepository(enabledSource, Repository.Provider.GetCoreV3()) : 
                    Repository.Factory.GetCoreV3(resourceUrl);

                resource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

                _metadataResources.Add(resourceUrl, resource);
            }

            return resource;
        }

        public async Task<IEnumerable<NuGetVersion>> GetAllVersions(string package, List<Uri> sources, bool includePrerelease, NuGetFramework targetFramework)
        {
            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                try
                {
                    var metadata = await FindMetadataResourceForSource(source);

                    var reducer = new FrameworkReducer();

                    var compatibleMetadataList = (await metadata.GetMetadataAsync(package, includePrerelease, false, _context, NullLogger.Instance, CancellationToken.None))
                        .OfType<PackageSearchMetadata>()
                        .Where(meta => reducer.GetNearest(targetFramework, meta.DependencySets.Select(ds => ds.TargetFramework)) != null);

                    allVersions.AddRange(compatibleMetadataList.Select(m => m.Version));
                }
                catch(HttpRequestException)
                {
                    // Suppress HTTP errors when connecting to NuGet sources 
                }
            }

            return allVersions;
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }

}