using NuGet.Client;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
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

    internal sealed class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        // The enabled sources and the package source mapping come from the same settings and are published together,
        // so a concurrent first call never sees sources without their mapping (which would query unmapped sources).
        private SourceSettings? _sourceSettings;

        private readonly SourceCacheContext _context;

        private readonly ConcurrentDictionary<string, Task<PackageMetadataResource?>> _metadataResourceRequests = [];

        // The result of checking a package version against a target framework, null when the source doesn't have it.
        private readonly ConcurrentDictionary<(string Source, string Id, NuGetVersion Version, NuGetFramework Framework, bool IsDependency), Task<bool?>> _supportChecks = [];

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

        /// <summary>
        /// The repository for a source, created from the enabled sources in the NuGet settings when it is one of them,
        /// so that its credentials apply; <see langword="null"/> when package source mapping doesn't map the package to it.
        /// </summary>
        private SourceRepository? GetSourceRepository(Uri source, string? projectFilePath, string packageId)
        {
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

            return enabledSource != null
                       ? new SourceRepository(enabledSource, Repository.Provider.GetCoreV3())
                       : Repository.Factory.GetCoreV3(source.AbsoluteUri);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This method is supposed to fail silently")]
        private async Task<PackageMetadataResource?> FindMetadataResourceForSource(Uri source, string? projectFilePath, string packageId, CancellationToken cancellationToken)
        {
            try
            {
                string resourceUrl = source.AbsoluteUri;
                var sourceRepository = GetSourceRepository(source, projectFilePath, packageId);
                if (sourceRepository == null)
                {
                    return null;
                }

                // The discovery request is cached per source and shared, so it doesn't take this caller's token; the caller
                // only stops waiting for it.
                var metadataResourceRequest = _metadataResourceRequests.GetOrAdd(resourceUrl, _ => sourceRepository.GetResourceAsync<PackageMetadataResource>());

                return await metadataResourceRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
                    var metadata = await FindMetadataResourceForSource(source, projectFilePath, package, cancellationToken).ConfigureAwait(false);
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
                catch (Exception ex) when (ignoreFailedSources || ex.InnerException is HttpRequestException)
                {
                    // Like `dotnet restore --ignore-failed-sources`, the option skips a source that fails and lets the
                    // others answer. Without it, a failure is an error rather than a silently incomplete list of versions,
                    // except for HTTP errors (NuGet wraps them in a FatalProtocolException), which are always suppressed
                    // like the HttpRequestException above.
                }
            }

            return allVersions;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A source that fails is skipped, like one that doesn't have the package")]
        public async Task<bool> DeclaresTargetFrameworkUnsupportedAsync(TargetFrameworkSupportQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.TargetFramework.Framework != FrameworkConstants.FrameworkIdentifiers.NetCoreApp)
            {
                return false;
            }

            foreach (var source in query.Sources)
            {
                SourceRepository? repository;
                try
                {
                    repository = GetSourceRepository(source, query.ProjectFilePath, query.PackageId);
                }
                catch (Exception)
                {
                    continue;
                }

                if (repository == null)
                {
                    continue;
                }

                var unsupported = await CheckAsync(repository, new PackageIdentity(query.PackageId, query.Version), query.TargetFramework, isDependency: false)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (unsupported != null)
                {
                    return unsupported.Value;
                }
            }

            return false;
        }

        // Shared by every caller with the same key, so it doesn't take one caller's cancellation token.
        private Task<bool?> CheckAsync(SourceRepository repository, PackageIdentity identity, NuGetFramework targetFramework, bool isDependency) =>
            _supportChecks.GetOrAdd(
                (repository.PackageSource.Source, identity.Id.ToUpperInvariant(), identity.Version, targetFramework, isDependency),
                _ => ReadDeclaresUnsupportedAsync(repository, identity, targetFramework, isDependency));

        /// <summary>
        /// Whether the package declares <paramref name="targetFramework"/> unsupported, or <see langword="null"/> when
        /// the source doesn't have it. A dependency (<paramref name="isDependency"/>) is checked on its own MSBuild
        /// files only, which it imports through <c>buildTransitive</c>; the package itself also through <c>build</c>,
        /// and through the dependencies it brings in for the framework (at their minimum versions, one level deep):
        /// packages such as Microsoft.Extensions.Diagnostics.HealthChecks have no targets of their own but depend on
        /// ones that warn.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A package that can't be inspected is treated as supported")]
        private async Task<bool?> ReadDeclaresUnsupportedAsync(SourceRepository repository, PackageIdentity identity, NuGetFramework targetFramework, bool isDependency)
        {
            try
            {
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>().ConfigureAwait(false);
                if (resource == null)
                {
                    return null;
                }

                var info = await resource.GetDependencyInfoAsync(identity.Id, identity.Version, _context, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                if (info == null)
                {
                    return null;
                }

                // Only a version that has assets for some .NET versions but reaches this one through netstandard can
                // have dropped it, so only such a version is downloaded.
                if (!FallsBackToNetStandard(targetFramework, info.DependencyGroups))
                {
                    return false;
                }

                using var package = new MemoryStream();
                if (!await resource.CopyNupkgToStreamAsync(identity.Id, identity.Version, package, _context, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false))
                {
                    return false;
                }

                package.Position = 0;
                if (await TargetsDeclareUnsupportedAsync(package, targetFramework, includeBuild: !isDependency).ConfigureAwait(false))
                {
                    return true;
                }

                if (isDependency)
                {
                    return false;
                }

                var dependencies = NuGetFrameworkUtility.GetNearest(info.DependencyGroups, targetFramework, group => group.TargetFramework);
                var dependencyChecks = await Task.WhenAll((dependencies?.Packages ?? [])
                    .Where(dependency => dependency.VersionRange.MinVersion != null)
                    .Select(dependency => CheckAsync(repository, new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion), targetFramework, isDependency: true)))
                    .ConfigureAwait(false);
                return dependencyChecks.Any(unsupported => unsupported == true);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool FallsBackToNetStandard(NuGetFramework targetFramework, IEnumerable<PackageDependencyGroup> dependencyGroups)
        {
            var frameworks = dependencyGroups.Select(group => group.TargetFramework).ToList();
            var nearest = new FrameworkReducer().GetNearest(targetFramework, frameworks);
            return nearest?.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard &&
                   frameworks.Any(framework => framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp);
        }

        // Whether the .targets files restore selects for the framework hold the NETStandardCompatError_* target the
        // .NET runtime packages generate to warn that it isn't supported. Restore takes buildTransitive when the
        // package has it for the framework, and build otherwise (for a direct reference only).
        private static async Task<bool> TargetsDeclareUnsupportedAsync(Stream package, NuGetFramework targetFramework, bool includeBuild)
        {
            using var reader = new PackageArchiveReader(package, leaveStreamOpen: true);
            var conventions = new ManagedCodeConventions(new RuntimeGraph());
            var items = new ContentItemCollection();
            items.Load(reader.GetFiles());

            var criteria = conventions.Criteria.ForFramework(targetFramework);
            var group = items.FindBestItemGroup(criteria, conventions.Patterns.MSBuildTransitiveFiles)
                        ?? (includeBuild ? items.FindBestItemGroup(criteria, conventions.Patterns.MSBuildFiles) : null);
            foreach (var item in group?.Items.Where(i => i.Path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                using var file = new StreamReader(reader.GetStream(item.Path));
                if ((await file.ReadToEndAsync().ConfigureAwait(false)).Contains("NETStandardCompatError_", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
