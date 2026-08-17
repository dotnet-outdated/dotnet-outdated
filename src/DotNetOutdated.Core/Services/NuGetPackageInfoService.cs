using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
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

        private ISettings _settings;

        private PackageSourceMapping _packageSourceMapping;

        private readonly SourceCacheContext _context;

        private readonly ConcurrentDictionary<string, Task<PackageMetadataResource>> _metadataResourceRequests = [];

        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _repositoryUrlRequests = [];

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
                _settings = Settings.LoadDefaultSettings(root);
                _enabledSources = SettingsUtility.GetEnabledSources(_settings);
                _packageSourceMapping = PackageSourceMapping.GetPackageSourceMapping(_settings);
            }

            return _enabledSources;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This method is supposed to fail silently")]
        private SourceRepository FindSourceRepositoryForSource(Uri source, string projectFilePath, string packageId)
        {
            try
            {
                string resourceUrl = source.AbsoluteUri;

                // We try and create the source repository from the enable sources we loaded from config.
                // This allows us to inherit the username/password for the source from the config and thus
                // enables secure feeds to work properly
                var enabledSources = this.GetEnabledSources(projectFilePath);
                var enabledSource = enabledSources?.FirstOrDefault(s => s.SourceUri == source);


                if (enabledSource != null && _packageSourceMapping.IsEnabled)
                {
                    var mappedSources = _packageSourceMapping.GetConfiguredPackageSources(packageId);
                    if (mappedSources != null && !mappedSources.Any(s => string.Equals(s, enabledSource.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Skip package sources that are not mapped to the package
                        return null;
                    }
                }

                return enabledSource != null
                                           ? new SourceRepository(enabledSource, Repository.Provider.GetCoreV3())
                                           : Repository.Factory.GetCoreV3(resourceUrl);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This method is supposed to fail silently")]
        private async Task<PackageMetadataResource> FindMetadataResourceForSource(Uri source, string projectFilePath, string packageId)
        {
            try
            {
                var sourceRepository = FindSourceRepositoryForSource(source, projectFilePath, packageId);
                if (sourceRepository == null)
                    return null;

                string resourceUrl = source.AbsoluteUri;
                var metadataResourceRequest = _metadataResourceRequests.GetOrAdd(resourceUrl, _ => sourceRepository.GetResourceAsync<PackageMetadataResource>());

                return await metadataResourceRequest.ConfigureAwait(false);
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
            ArgumentNullException.ThrowIfNull(sources);

            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                try
                {
                    var metadata = await FindMetadataResourceForSource(source, projectFilePath, package).ConfigureAwait(false);
                    if (metadata != null)
                    {
                        var compatibleMetadataList = await metadata.GetMetadataAsync(package, includePrerelease, false, _context, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

                        if (olderThanDays > 0)
                        {
                            compatibleMetadataList = compatibleMetadataList.Where(c => !c.Published.HasValue ||
                                                                                       c.Published <= DateTimeOffset.UtcNow.AddDays(-olderThanDays));
                        }

                        // We need to ensure that we only get package versions which are compatible with the requested target framework.
                        // For development dependencies, we do not perform this check
                        if (!isDevelopmentDependency)
                        {
                            var reducer = new FrameworkReducer();

                            compatibleMetadataList = compatibleMetadataList
                                .Where(meta => meta.DependencySets?.Any() != true ||
                                               reducer.GetNearest(targetFramework, meta.DependencySets.Select(ds => ds.TargetFramework)) != null);
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

        public async Task<string> GetRepositoryUrl(string package, NuGetVersion version, IEnumerable<Uri> sources, string projectFilePath)
        {
            ArgumentNullException.ThrowIfNull(sources);

            var sourceList = sources.ToList();
            string cacheKey = string.Join("|", package, version, projectFilePath, string.Join(";", sourceList.Select(s => s.AbsoluteUri)));
            var request = new Lazy<Task<string>>(() => GetRepositoryUrlCore(package, version, sourceList, projectFilePath));
            return await _repositoryUrlRequests.GetOrAdd(cacheKey, request).Value.ConfigureAwait(false);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Repository metadata must not fail package analysis")]
        private async Task<string> GetRepositoryUrlCore(string package, NuGetVersion version, IReadOnlyList<Uri> sources, string projectFilePath)
        {
            try
            {
                GetEnabledSources(projectFilePath);
                string globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(_settings);
                var pathResolver = new VersionFolderPathResolver(globalPackagesFolder);
                string installPath = pathResolver.GetInstallPath(package, version);

                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                {
                    using var packageReader = new PackageFolderReader(installPath);
                    return await GetRepositoryUrl(packageReader).ConfigureAwait(false);
                }

                foreach (var source in sources)
                {
                    try
                    {
                        var sourceRepository = FindSourceRepositoryForSource(source, projectFilePath, package);
                        if (sourceRepository == null)
                            continue;

                        var downloadResource = await sourceRepository.GetResourceAsync<DownloadResource>().ConfigureAwait(false);
                        var downloadContext = new PackageDownloadContext(_context);
                        using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                            new PackageIdentity(package, version),
                            downloadContext,
                            globalPackagesFolder,
                            NullLogger.Instance,
                            CancellationToken.None).ConfigureAwait(false);

                        if (downloadResult.PackageReader != null)
                            return await GetRepositoryUrl(downloadResult.PackageReader).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static async Task<string> GetRepositoryUrl(PackageReaderBase packageReader)
        {
            var nuspecReader = await packageReader.GetNuspecReaderAsync(CancellationToken.None).ConfigureAwait(false);
            string repositoryUrl = nuspecReader.GetRepositoryMetadata()?.Url;
            return string.IsNullOrWhiteSpace(repositoryUrl) ? nuspecReader.GetProjectUrl() : repositoryUrl;
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
