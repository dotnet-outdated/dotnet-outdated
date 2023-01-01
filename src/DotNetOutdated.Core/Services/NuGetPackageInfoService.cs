using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    using System.Collections.Concurrent;

    public sealed class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        private IEnumerable<PackageSource> _enabledSources;
        private readonly SourceCacheContext _context;

        private readonly ConcurrentDictionary<string, Lazy<Task<PackageMetadataResource>>> _metadataResourceRequests = new ConcurrentDictionary<string, Lazy<Task<PackageMetadataResource>>>();

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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This method is supposed to fail silently")]
        private async Task<PackageMetadataResource> FindMetadataResourceForSource(Uri source, string projectFilePath)
        {
            try
            {
                string resourceUrl = source.AbsoluteUri;

                // We try and create the source repository from the enable sources we loaded from config.
                // This allows us to inherit the username/password for the source from the config and thus
                // enables secure feeds to work properly
                var enabledSources = this.GetEnabledSources(projectFilePath);
                var enabledSource = enabledSources?.FirstOrDefault(s => s.SourceUri == source);
                var sourceRepository = enabledSource != null
                                           ? new SourceRepository(enabledSource, Repository.Provider.GetCoreV3())
                                           : Repository.Factory.GetCoreV3(resourceUrl);

                var resourceRequest = new Lazy<Task<PackageMetadataResource>>(() => sourceRepository.GetResourceAsync<PackageMetadataResource>());
                return await _metadataResourceRequests.GetOrAdd(resourceUrl, resourceRequest).Value.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<NuGetVersion>> GetAllVersions(string package, IEnumerable<Uri> sources, bool includePrerelease, NuGetFramework targetFramework,
            string projectFilePath, bool isDevelopmentDependency)
        {
            return await GetAllVersions(package, sources, includePrerelease, targetFramework, projectFilePath, isDevelopmentDependency, 0).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<NuGetVersion>> GetAllVersions(string package, IEnumerable<Uri> sources, bool includePrerelease, NuGetFramework targetFramework,
            string projectFilePath, bool isDevelopmentDependency, int olderThanDays, bool ignoreFailedSources = false)
        {
            if (sources == null)
                throw new ArgumentNullException(nameof(sources));

            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                try
                {
                    var metadata = await FindMetadataResourceForSource(source, projectFilePath).ConfigureAwait(false);
                    if (metadata != null)
                    {
                        var compatibleMetadataList = (await metadata.GetMetadataAsync(package, includePrerelease, false, _context, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false)).ToList();

                        if (olderThanDays > 0)
                        {
                            compatibleMetadataList = compatibleMetadataList.Where(c => !c.Published.HasValue ||
                                                                                       c.Published <= DateTimeOffset.UtcNow.AddDays(-olderThanDays)).ToList();
                        }

                        // We need to ensure that we only get package versions which are compatible with the requested target framework.
                        // For development dependencies, we do not perform this check
                        if (!isDevelopmentDependency)
                        {
                            var reducer = new FrameworkReducer();

                            compatibleMetadataList = compatibleMetadataList
                                .Where(meta => meta.DependencySets?.Any() != true ||
                                               reducer.GetNearest(targetFramework, meta.DependencySets.Select(ds => ds.TargetFramework)) != null)
                                .ToList();
                        }

                        foreach (var m in compatibleMetadataList)
                        {
                            if (m is PackageSearchMetadata packageSearchMetadata)
                            {
                                allVersions.Add(packageSearchMetadata.Version);
                            }
                            else if (m is PackageSearchMetadataV2Feed packageSearchMetadataV2Feed)
                            {
                                allVersions.Add(packageSearchMetadataV2Feed.Version);
                            }
                            else if (m is LocalPackageSearchMetadata localPackageSearchMetadata)
                            {
                                allVersions.Add(localPackageSearchMetadata.Identity.Version);
                            }
                            else
                            {
                                allVersions.Add(m.Identity.Version);
                            }
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    // Suppress HTTP errors when connecting to NuGet sources
                }
                catch (Exception ex)
                {
                    if (!ignoreFailedSources)
                    {
                        continue;
                    }
                    // if the inner exception is NOT HttpRequestException, throw it
                    if (ex.InnerException != null && !(ex.InnerException is HttpRequestException)) throw;
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
