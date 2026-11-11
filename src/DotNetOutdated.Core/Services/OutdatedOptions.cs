using System.Collections.Generic;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>Which dependencies <see cref="IOutdatedDependencyService.FindOutdatedAsync"/> reports, and how it finds their latest versions.</summary>
public sealed record OutdatedOptions
{
    /// <summary>The default options: newest version of any major, prereleases only for prerelease references.</summary>
    public static OutdatedOptions Default { get; } = new();

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
