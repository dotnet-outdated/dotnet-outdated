using NuGet.Client;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.RuntimeModel;
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

    internal sealed class NuGetPackageInfoService : INuGetPackageInfoService, IDisposable
    {
        // The enabled sources and the package source mapping come from the same settings and are published together,
        // so a concurrent first call never sees sources without their mapping (which would query unmapped sources).
        private SourceSettings? _sourceSettings;

        private readonly SourceCacheContext _context;

        private readonly ConcurrentDictionary<string, Task<PackageMetadataResource?>> _metadataResourceRequests = [];

        private readonly ConcurrentDictionary<(string Id, NuGetVersion Version, NuGetFramework Framework, bool CheckDependencies), Task<bool>> _unsupportedFrameworkChecks = [];

        // Limits the packages downloaded (and held in memory) at once for the unsupported framework check.
        private readonly SemaphoreSlim _packageDownloads = new(MaxConcurrentPackageDownloads);

        private const int MaxConcurrentPackageDownloads = 8;

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
        private async Task<(SourceRepository Repository, PackageMetadataResource? Metadata)?> FindMetadataResourceForSource(Uri source, string? projectFilePath, string packageId, CancellationToken cancellationToken)
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

                // The discovery request is cached per source and shared, so it doesn't take this caller's token; the caller
                // only stops waiting for it.
                var metadataResourceRequest = _metadataResourceRequests.GetOrAdd(resourceUrl, _ => sourceRepository.GetResourceAsync<PackageMetadataResource>());

                return (sourceRepository, await metadataResourceRequest.WaitAsync(cancellationToken).ConfigureAwait(false));
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
                    var resources = await FindMetadataResourceForSource(source, projectFilePath, package, cancellationToken).ConfigureAwait(false);
                    var metadata = resources?.Metadata;
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

                            // A version that only reaches a .NET target through a netstandard fallback, while it has assets
                            // for newer .NET versions, may declare that target unsupported with a build warning. Finding
                            // out means downloading it, so only the newest version of each set of dependency-group
                            // frameworks is checked, and the others with the same frameworks share its result: the
                            // runtime packages generate the warning from those frameworks, so the versions of a release
                            // line (9.0.x, 10.0.x) agree.
                            if (!query.AllowUnsupportedFrameworks)
                            {
                                var repository = resources!.Value.Repository;
                                var candidates = compatibleMetadataList.ToList();
                                var fallbackGroups = candidates
                                    .Where(meta => FallsBackToNetStandard(targetFramework, meta))
                                    .GroupBy(DependencyFrameworksKey, StringComparer.Ordinal)
                                    .ToList();
                                var unsupported = await Task.WhenAll(fallbackGroups.Select(group =>
                                    DeclaresFrameworkUnsupportedAsync(repository, group.MaxBy(meta => meta.Identity.Version)!.Identity,
                                        targetFramework, checkDependencies: true, cancellationToken))).ConfigureAwait(false);
                                var unsupportedKeys = fallbackGroups.Where((_, index) => unsupported[index]).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                                compatibleMetadataList = candidates
                                    .Where(meta => !FallsBackToNetStandard(targetFramework, meta) || !unsupportedKeys.Contains(DependencyFrameworksKey(meta)))
                                    .ToList();
                            }
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

        private static bool FallsBackToNetStandard(NuGetFramework targetFramework, IPackageSearchMetadata meta)
        {
            if (targetFramework.Framework != FrameworkConstants.FrameworkIdentifiers.NetCoreApp || meta.DependencySets == null)
            {
                return false;
            }

            var frameworks = meta.DependencySets.Select(ds => ds.TargetFramework).ToList();
            var nearest = new FrameworkReducer().GetNearest(targetFramework, frameworks);
            return nearest?.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard &&
                   frameworks.Any(f => f.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp);
        }

        private static string DependencyFrameworksKey(IPackageSearchMetadata meta) =>
            string.Join(";", (meta.DependencySets ?? []).Select(ds => ds.TargetFramework.DotNetFrameworkName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Whether the package's MSBuild files for <paramref name="targetFramework"/>, selected the way restore selects
        /// them, contain the <c>NETStandardCompatError_*</c> target the .NET runtime packages use to warn that a target
        /// framework is not supported. With <paramref name="checkDependencies"/>, also whether one of the dependencies it
        /// brings in for that framework (at their minimum versions, one level deep) does: packages such as
        /// Microsoft.Extensions.Diagnostics.HealthChecks carry no targets of their own but depend on ones that warn.
        /// Fails open: a package that can't be downloaded or read is treated as supported.
        /// </summary>
        private Task<bool> DeclaresFrameworkUnsupportedAsync(SourceRepository repository, PackageIdentity identity, NuGetFramework targetFramework, bool checkDependencies, CancellationToken cancellationToken) =>
            _unsupportedFrameworkChecks.GetOrAdd(
                (identity.Id.ToUpperInvariant(), identity.Version, targetFramework, checkDependencies),
                _ => ReadDeclaresFrameworkUnsupportedAsync(repository, identity, targetFramework, checkDependencies)).WaitAsync(cancellationToken);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A package that can't be inspected is treated as supported")]
        private async Task<bool> ReadDeclaresFrameworkUnsupportedAsync(SourceRepository repository, PackageIdentity identity, NuGetFramework targetFramework, bool checkDependencies)
        {
            try
            {
                var (declaresUnsupported, dependencies) = await InspectPackageAsync(repository, identity, targetFramework, checkDependencies).ConfigureAwait(false);
                if (declaresUnsupported)
                {
                    return true;
                }

                // Checked after the download slot is released, as each dependency takes one of its own.
                var dependencyChecks = await Task.WhenAll((dependencies?.Packages ?? [])
                    .Where(dependency => dependency.VersionRange.MinVersion != null)
                    .Select(dependency => DeclaresFrameworkUnsupportedAsync(repository,
                        new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion), targetFramework, checkDependencies: false, CancellationToken.None)))
                    .ConfigureAwait(false);
                return dependencyChecks.Any(unsupported => unsupported);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads the package and reads whether its MSBuild files for <paramref name="targetFramework"/> declare it
        /// unsupported, and, with <paramref name="readDependencies"/>, the dependencies it brings in for that framework.
        /// Holds one of the limited download slots, as each package is held in memory while it is read.
        /// </summary>
        private async Task<(bool DeclaresUnsupported, PackageDependencyGroup? Dependencies)> InspectPackageAsync(SourceRepository repository, PackageIdentity identity, NuGetFramework targetFramework, bool readDependencies)
        {
            // Shared through the cache, so it doesn't take one caller's cancellation token.
            await _packageDownloads.WaitAsync().ConfigureAwait(false);
            try
            {
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>().ConfigureAwait(false);
                using var package = new MemoryStream();
                if (resource == null || !await resource.CopyNupkgToStreamAsync(identity.Id, identity.Version, package, _context, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false))
                {
                    return (false, null);
                }

                package.Position = 0;
                using var reader = new PackageArchiveReader(package);
                var conventions = new ManagedCodeConventions(new RuntimeGraph());
                var items = new ContentItemCollection();
                items.Load(reader.GetFiles());

                // Like restore: buildTransitive applies when the package has it for this framework, build otherwise.
                var criteria = conventions.Criteria.ForFramework(targetFramework);
                var group = items.FindBestItemGroup(criteria, conventions.Patterns.MSBuildTransitiveFiles)
                            ?? items.FindBestItemGroup(criteria, conventions.Patterns.MSBuildFiles);
                foreach (var item in group?.Items.Where(i => i.Path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)) ?? [])
                {
                    using var file = new StreamReader(reader.GetStream(item.Path));
                    if ((await file.ReadToEndAsync().ConfigureAwait(false)).Contains("NETStandardCompatError_", StringComparison.Ordinal))
                    {
                        return (true, null);
                    }
                }

                var dependencies = readDependencies
                    ? NuGetFrameworkUtility.GetNearest(reader.GetPackageDependencies(), targetFramework, g => g.TargetFramework)
                    : null;
                return (false, dependencies);
            }
            finally
            {
                _packageDownloads.Release();
            }
        }

        public void Dispose()
        {
            _context?.Dispose();
            _packageDownloads.Dispose();
        }
    }
}
