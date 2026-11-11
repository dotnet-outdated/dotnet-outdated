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
        // The enabled sources and the package source mapping come from the same settings and are published together,
        // so a concurrent first call never sees sources without their mapping (which would query unmapped sources).
        private SourceSettings? _sourceSettings;

        private readonly SourceCacheContext _context;

        private readonly ConcurrentDictionary<string, Task<PackageMetadataResource?>> _metadataResourceRequests = [];

        public NuGetPackageInfoService()
        {
            _context = new SourceCacheContext()
            {
                NoCache = true
            };
        }

        private sealed record SourceSettings(IEnumerable<PackageSource> EnabledSources, PackageSourceMapping PackageSourceMapping);

        private SourceSettings GetSourceSettings(string? root)
        {
            if (_sourceSettings == null)
            {
                var settings = Settings.LoadDefaultSettings(root);
                _sourceSettings = new SourceSettings(
                    SettingsUtility.GetEnabledSources(settings),
                    PackageSourceMapping.GetPackageSourceMapping(settings));
            }

            return _sourceSettings;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This method is supposed to fail silently")]
        private async Task<PackageMetadataResource?> FindMetadataResourceForSource(Uri source, string? projectFilePath, string packageId)
        {
            try
            {
                string resourceUrl = source.AbsoluteUri;

                // We try and create the source repository from the enable sources we loaded from config.
                // This allows us to inherit the username/password for the source from the config and thus
                // enables secure feeds to work properly
                var sourceSettings = GetSourceSettings(projectFilePath);
                var enabledSource = sourceSettings.EnabledSources.FirstOrDefault(s => s.SourceUri == source);


                if (enabledSource != null && sourceSettings.PackageSourceMapping.IsEnabled)
                {
                    var mappedSources = sourceSettings.PackageSourceMapping.GetConfiguredPackageSources(packageId);
                    if (mappedSources != null && !mappedSources.Any(s => string.Equals(s, enabledSource.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Skip package sources that are not mapped to the package
                        return null;
                    }
                }

                var sourceRepository = enabledSource != null
                                           ? new SourceRepository(enabledSource, Repository.Provider.GetCoreV3())
                                           : Repository.Factory.GetCoreV3(resourceUrl);

                var metadataResourceRequest = _metadataResourceRequests.GetOrAdd(resourceUrl, _ => sourceRepository.GetResourceAsync<PackageMetadataResource>());

                return await metadataResourceRequest.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(PackageVersionsQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var package = query.PackageId;
            var sources = query.Sources;
            var includePrerelease = query.IncludePrerelease;
            var targetFramework = query.TargetFramework;
            var projectFilePath = query.ProjectFilePath;
            var isDevelopmentDependency = query.IsDevelopmentDependency;
            var olderThanDays = query.OlderThanDays;
            var ignoreFailedSources = query.IgnoreFailedSources;

            var allVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                try
                {
                    var metadata = await FindMetadataResourceForSource(source, projectFilePath, package).ConfigureAwait(false);
                    if (metadata != null)
                    {
                        var compatibleMetadataList = await metadata.GetMetadataAsync(package, includePrerelease, false, _context, NullLogger.Instance, cancellationToken).ConfigureAwait(false);

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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
