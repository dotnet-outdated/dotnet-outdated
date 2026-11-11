using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Reports which dependencies of analyzed projects have newer versions, applying the filters and version settings of
/// <see cref="OutdatedOptions"/>. Up to one project per processor is looked up at a time, with the target frameworks and
/// dependencies of each project looked up concurrently.
/// </summary>
internal sealed class OutdatedDependencyService(INuGetPackageResolutionService resolutionService) : IOutdatedDependencyService
{
    private readonly INuGetPackageResolutionService _resolutionService = resolutionService;

    public async Task<IReadOnlyList<OutdatedProject>> FindOutdatedAsync(
        IEnumerable<Project> projects,
        OutdatedOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        options ??= OutdatedOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var results = new ConcurrentBag<OutdatedProject>();
        await Parallel.ForEachAsync(projects, cancellationToken, async (project, token) =>
        {
            if (await FindOutdatedAsync(project, options, token).ConfigureAwait(false) is { } outdated)
            {
                results.Add(outdated);
            }
        }).ConfigureAwait(false);

        return results
            .OrderBy(project => project.Name)
            .ThenBy(project => project.FilePath)
            .ToList();
    }

    private async Task<OutdatedProject?> FindOutdatedAsync(Project project, OutdatedOptions options, CancellationToken cancellationToken)
    {
        var frameworks = await Task.WhenAll(project.TargetFrameworks.Select(targetFramework =>
            FindOutdatedAsync(project, targetFramework, options, cancellationToken))).ConfigureAwait(false);

        var outdatedFrameworks = frameworks
            .OfType<OutdatedTargetFramework>()
            .OrderBy(targetFramework => targetFramework.Name.Framework)
            .ToList();

        return outdatedFrameworks.Count == 0
            ? null
            : new OutdatedProject { Name = project.Name, FilePath = project.FilePath, TargetFrameworks = outdatedFrameworks };
    }

    private async Task<OutdatedTargetFramework?> FindOutdatedAsync(
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

        return outdatedDependencies.Count == 0
            ? null
            : new OutdatedTargetFramework
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

    // The effective upper bound is whichever of the declared range and the maximum version is lower, so pinned
    // versions ([1.0.0]) and bounded ranges ([1.0.0, 2.0.0)) are never widened.
    private static VersionRange CapAtMaximumVersion(VersionRange range, NuGetVersion? maximumVersion) =>
        maximumVersion is not null && (range.MaxVersion is null || maximumVersion < range.MaxVersion)
            ? new VersionRange(range.MinVersion, range.IsMinInclusive, maximumVersion, includeMaxVersion: true, range.Float, range.OriginalString)
            : range;

    private static bool NameContains(Dependency dependency, string part) =>
        dependency.Name.Contains(part, StringComparison.InvariantCultureIgnoreCase);
}
