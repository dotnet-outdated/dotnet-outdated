using System.Collections.Generic;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Which projects <see cref="IOutdatedDependencyService.FindOutdatedAsync"/> finds, which of their dependencies it
/// reports, and how it finds their latest versions.
/// </summary>
public sealed record OutdatedOptions
{
    /// <summary>
    /// The default options: the one project or solution at the path, direct references only, newest version of any
    /// major, prereleases only for prerelease references.
    /// </summary>
    public static OutdatedOptions Default { get; } = new();

    /// <summary>For a directory, find every project under it rather than the one solution or project in it.</summary>
    public bool Recursive { get; init; }

    /// <summary>With <see cref="Recursive"/>, also find file-based apps (<c>.cs</c> files outside any project).</summary>
    public bool IncludeFileBasedApps { get; init; }

    /// <summary>Also report the packages that references bring in, down to <see cref="TransitiveDepth"/> levels.</summary>
    public bool IncludeTransitiveDependencies { get; init; }

    /// <summary>How many levels of transitive dependencies to report; 1 for the dependencies of references only.</summary>
    public int TransitiveDepth { get; init; } = 1;

    /// <summary>The runtime identifier to restore for, if any.</summary>
    public string? Runtime { get; init; }

    /// <summary>Which part of the resolved version must stay the same.</summary>
    public VersionLock VersionLock { get; init; } = VersionLock.None;

    /// <summary>Whether prerelease versions are considered.</summary>
    public PrereleaseReporting Prerelease { get; init; } = PrereleaseReporting.Auto;

    /// <summary>For a prerelease reference, the prerelease label a new version must start with.</summary>
    public string? PrereleaseLabel { get; init; }

    /// <summary>Ignore versions published fewer than this many days ago; 0 to consider every version.</summary>
    public int OlderThanDays { get; init; }

    /// <summary>The highest version to consider, on top of the range each reference declares.</summary>
    public NuGetVersion? MaximumVersion { get; init; }

    /// <summary>Treat package sources that fail as having no versions rather than failing.</summary>
    public bool IgnoreFailedSources { get; init; }

    /// <summary>Report auto-referenced dependencies, such as those an SDK adds.</summary>
    public bool IncludeAutoReferences { get; init; }

    /// <summary>Report dependencies that are already at their latest version.</summary>
    public bool IncludeUpToDate { get; init; }

    /// <summary>Report only dependencies whose name contains one of these strings (case-insensitive); all when empty.</summary>
    public IReadOnlyList<string> IncludeFilters { get; init; } = [];

    /// <summary>Don't report dependencies whose name contains one of these strings (case-insensitive).</summary>
    public IReadOnlyList<string> ExcludeFilters { get; init; } = [];
}
