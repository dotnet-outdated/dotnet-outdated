using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Finds the projects at a path, analyzes them, and reports which of their dependencies have newer versions, applying
/// the filters and version settings of <see cref="OutdatedOptions"/>. Projects are analyzed concurrently. Up to one
/// project per processor is looked up at a time, with the target frameworks and dependencies of each project looked up
/// concurrently.
/// </summary>
internal sealed class OutdatedDependencyService(
    IProjectDiscoveryService discoveryService,
    IProjectAnalysisService analysisService,
    INuGetPackageResolutionService resolutionService,
    IFileSystem fileSystem) : IOutdatedDependencyService
{
    private readonly IProjectDiscoveryService _discoveryService = discoveryService;
    private readonly IProjectAnalysisService _analysisService = analysisService;
    private readonly INuGetPackageResolutionService _resolutionService = resolutionService;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<OutdatedReport> FindOutdatedAsync(
        string path,
        OutdatedOptions? options = null,
        IProgress<OutdatedReportStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= OutdatedOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(OutdatedReportStage.DiscoveringProjects);
        var projectPaths = _discoveryService.DiscoverProjects(path, options.Recursive, options.IncludeFileBasedApps);

        var analysisOptions = new ProjectAnalysisOptions
        {
            IncludeTransitiveDependencies = options.IncludeTransitiveDependencies,
            TransitiveDepth = options.TransitiveDepth,
            Runtime = options.Runtime,
        };
        progress?.Report(OutdatedReportStage.AnalyzingProjects);
        var analyzed = new ConcurrentBag<IReadOnlyList<Project>>();
        await Parallel.ForEachAsync(projectPaths, cancellationToken, async (projectPath, token) =>
            analyzed.Add(await _analysisService.AnalyzeProjectAsync(projectPath, analysisOptions, token).ConfigureAwait(false)))
            .ConfigureAwait(false);

        // A project's restore graph holds the projects it references, so a referenced project is analyzed both on its
        // own and with each project that references it (issue #830). Each project file is reported once, comparing paths
        // with symbolic links resolved, because MSBuild reports a referenced project by its real path (on macOS the temp
        // folder /var is a link to /private/var).
        var projects = analyzed
            .SelectMany(list => list)
            .DistinctBy(project => CanonicalPath(project.FilePath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        progress?.Report(OutdatedReportStage.FindingLatestVersions);
        return new OutdatedReport { Projects = await FindOutdatedAsync(projects, options, cancellationToken).ConfigureAwait(false) };
    }

    private async Task<IReadOnlyList<OutdatedProject>> FindOutdatedAsync(
        IReadOnlyList<Project> projects,
        OutdatedOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new ConcurrentBag<OutdatedProject>();
        await Parallel.ForEachAsync(projects, cancellationToken, async (project, token) =>
            results.Add(await FindOutdatedAsync(project, options, token).ConfigureAwait(false))).ConfigureAwait(false);

        return results
            .OrderBy(project => project.Name)
            .ThenBy(project => project.FilePath)
            .ToList();
    }

    private async Task<OutdatedProject> FindOutdatedAsync(Project project, OutdatedOptions options, CancellationToken cancellationToken)
    {
        var frameworks = await Task.WhenAll(project.TargetFrameworks.Select(targetFramework =>
            FindOutdatedAsync(project, targetFramework, options, cancellationToken))).ConfigureAwait(false);

        return new OutdatedProject
        {
            Name = project.Name,
            FilePath = project.FilePath,
            Sources = project.Sources,
            TargetFrameworks = [.. frameworks.OrderBy(targetFramework => targetFramework.Name.Framework)],
        };
    }

    private async Task<OutdatedTargetFramework> FindOutdatedAsync(
        Project project,
        TargetFramework targetFramework,
        OutdatedOptions options,
        CancellationToken cancellationToken)
    {
        var dependencies = targetFramework.Dependencies
            .Where(dependency => options.IncludeAutoReferences || !dependency.IsAutoReferenced)
            .Where(dependency => options.IncludeFilters.Count == 0 || options.IncludeFilters.Any(filter => NameContains(dependency, filter)))
            .Where(dependency => !options.ExcludeFilters.Any(filter => NameContains(dependency, filter)));

        var results = await Task.WhenAll(dependencies.Select(dependency =>
            FindOutdatedAsync(project, targetFramework, dependency, options, cancellationToken))).ConfigureAwait(false);

        var outdatedDependencies = results
            .OfType<OutdatedDependency>()
            .OrderBy(dependency => dependency.Dependency.Name)
            .ToList();

        return new OutdatedTargetFramework
        {
            Name = targetFramework.Name,
            TargetAlias = targetFramework.TargetAlias,
            Dependencies = outdatedDependencies,
        };
    }

    private async Task<OutdatedDependency?> FindOutdatedAsync(
        Project project,
        TargetFramework targetFramework,
        Dependency dependency,
        OutdatedOptions options,
        CancellationToken cancellationToken)
    {
        var resolvedVersion = dependency.ResolvedVersion;
        var allowedRange = CapAtMaximumVersion(dependency.VersionRange, options.MaximumVersion);

        // Otherwise the latest version is unknown, and OutdatedDependency.LatestVersionUnknownReason says why.
        LatestVersionQuery? query = OutdatedDependency.CanLookUpLatestVersion(dependency)
            ? new LatestVersionQuery
            {
                PackageId = dependency.Name,
                CurrentVersion = resolvedVersion!,
                AllowedRange = allowedRange,
                Sources = project.Sources.ToList(),
                TargetFramework = targetFramework.Name,
                ProjectFilePath = project.FilePath,
                IsDevelopmentDependency = dependency.IsDevelopmentDependency,
                VersionLock = options.VersionLock,
                Prerelease = options.Prerelease,
                PrereleaseLabel = options.PrereleaseLabel,
                OlderThanDays = options.OlderThanDays,
                IgnoreFailedSources = options.IgnoreFailedSources,
            }
            : null;

        var latestVersion = query == null
            ? null
            : await _resolutionService.ResolveLatestVersionAsync(query, cancellationToken).ConfigureAwait(false);

        // The resolution service returns the resolved version when nothing newer is allowed (including when no newer
        // version is older than OlderThanDays), so an unknown latest version is still reported.
        if (latestVersion != null && latestVersion == resolvedVersion && !options.IncludeUpToDate)
        {
            return null;
        }

        return new OutdatedDependency { Dependency = dependency, LatestVersion = latestVersion };
    }

    /// <summary>
    /// The full path of <paramref name="path"/> with every symbolic link along it resolved, so two paths to the same
    /// file compare equal. A part that can't be resolved is kept as it is.
    /// </summary>
    private string CanonicalPath(string path) => CanonicalPath(path, linksFollowed: 0);

    private string CanonicalPath(string path, int linksFollowed)
    {
        // As many links as the OS follows before it gives up (MAXSYMLINKS on Linux), so a loop of links ends.
        const int MaxLinksFollowed = 40;

        var fullPath = _fileSystem.Path.GetFullPath(path);
        var root = _fileSystem.Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;

        foreach (var segment in fullPath[root.Length..].Split(
            [_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = _fileSystem.Path.Combine(current, segment);

            try
            {
                if (linksFollowed < MaxLinksFollowed &&
                    _fileSystem.FileInfo.New(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target can itself be under a link (on macOS a link to /var/folders/... is under /private/var).
                    current = CanonicalPath(target.FullName, linksFollowed + 1);
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Not resolvable (e.g. a broken link or no permission): compare this part as written.
            }
        }

        return current;
    }

    // The effective upper bound is whichever of the declared range and the maximum version is lower, so pinned
    // versions ([1.0.0]) and bounded ranges ([1.0.0, 2.0.0)) are never widened.
    private static VersionRange CapAtMaximumVersion(VersionRange range, NuGetVersion? maximumVersion) =>
        maximumVersion is not null && (range.MaxVersion is null || maximumVersion < range.MaxVersion)
            ? new VersionRange(range.MinVersion, range.IsMinInclusive, maximumVersion, includeMaxVersion: true, range.Float, range.OriginalString)
            : range;

    private static bool NameContains(Dependency dependency, string part) =>
        dependency.Name.Contains(part, StringComparison.InvariantCultureIgnoreCase);
}
