using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
        private IEnumerable<PackageSource> _enabledSources = null;
        private readonly SourceCacheContext _context;
        private readonly Dictionary<string, PackageMetadataResource> _metadataResources = new Dictionary<string, PackageMetadataResource>();

        public NuGetPackageInfoService()
        {
            _context = new SourceCacheContext()
            {
                NoCache = true
            };
        }

        private IEnumerable<PackageSource> GetEnabledSources(string root)
        {
            if (_enabledSources == null)
            {
                var settings = Settings.LoadDefaultSettings(root);
                _enabledSources = SettingsUtility.GetEnabledSources(settings);
            }

            return _enabledSources;
        }
        
        private async Task<PackageMetadataResource> FindMetadataResourceForSource(Uri source, string projectFilePath)
        {
            try
            {
                string resourceUrl = source.AbsoluteUri;

                var resource = _metadataResources.GetValueOrDefault(resourceUrl);
                if (resource == null)
                {
                    // We try and create the source repository from the enable sources we loaded from config.
                    // This allows us to inherit the username/password for the source from the config and thus
                    // enables secure feeds to work properly
                    var enabledSources = GetEnabledSources(projectFilePath);
                    var enabledSource = enabledSources?.FirstOrDefault(s => s.SourceUri == source);
                    var sourceRepository = enabledSource != null ? 
                        new SourceRepository(enabledSource, Repository.Provider.GetCoreV3()) : 
                        Repository.Factory.GetCoreV3(resourceUrl);

                    resource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

                    _metadataResources.Add(resourceUrl, resource);
                }

                return resource;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        public async Task<IEnumerable<NuGetVersion>> GetAllVersions(string package, List<Uri> sources, bool includePrerelease, NuGetFramework targetFramework,
            string projectFilePath)
        {
            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                try
                {
                    var metadata = await FindMetadataResourceForSource(source, projectFilePath);
                    if (metadata != null)
                    {
                        var reducer = new FrameworkReducer();

                        // We need to ensure that we only get package versions which are compatible with the requested target framework. For certain package types (such as 
                        // Roslyn Analyzers) there is no target framework listed for the actual package itself, as it does not contain libraries. So we need to also allow package
                        // versions where there are no dependency sets listed
                        var compatibleMetadataList = (await metadata.GetMetadataAsync(package, includePrerelease, false, _context, NullLogger.Instance, CancellationToken.None))
                            .OfType<PackageSearchMetadata>()
                            .Where(meta => meta.DependencySets == null || !meta.DependencySets.Any() ||
                                           reducer.GetNearest(targetFramework, meta.DependencySets.Select(ds => ds.TargetFramework)) != null);

                        allVersions.AddRange(compatibleMetadataList.Select(m => m.Version));
                    }
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