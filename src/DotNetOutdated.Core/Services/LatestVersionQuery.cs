using System;
using System.Collections.Generic;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Which version of a package to find with <see cref="INuGetPackageResolutionService.ResolveLatestVersionAsync"/>:
/// the newest one that the current version may move to, given the version lock and prerelease settings.
/// </summary>
public sealed record LatestVersionQuery
{
    /// <summary>The package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>The version the project resolves today. The result is never older than this version.</summary>
    public required NuGetVersion CurrentVersion { get; init; }

    /// <summary>
    /// The range the new version must fall in, usually the range the project declares. It must have a lower bound.
    /// </summary>
    public required VersionRange AllowedRange { get; init; }

    /// <summary>The package sources to search.</summary>
    public required IReadOnlyList<Uri> Sources { get; init; }

    /// <summary>
    /// The target framework the package must support. Versions with no dependency group compatible with it are skipped,
    /// unless <see cref="IsDevelopmentDependency"/> is set.
    /// </summary>
    public required NuGetFramework TargetFramework { get; init; }

    /// <summary>
    /// The project whose NuGet settings (source credentials and package source mapping) apply; <see langword="null"/>
    /// for the user's and machine's settings only.
    /// </summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>Whether the package is a development dependency, which skips the target framework check.</summary>
    public bool IsDevelopmentDependency { get; init; }

    /// <summary>Which part of the current version must stay the same.</summary>
    public VersionLock VersionLock { get; init; } = VersionLock.None;

    /// <summary>Whether prerelease versions are considered.</summary>
    public PrereleaseReporting Prerelease { get; init; } = PrereleaseReporting.Auto;

    /// <summary>
    /// For a prerelease current version, the prerelease label a new version must start with; the current version's
    /// first label when <see langword="null"/> or empty.
    /// </summary>
    public string? PrereleaseLabel { get; init; }

    /// <summary>Ignore versions published fewer than this many days ago; 0 to consider every version.</summary>
    public int OlderThanDays { get; init; }

    /// <summary>Treat package sources that fail as having no versions rather than failing.</summary>
    public bool IgnoreFailedSources { get; init; }

    /// <inheritdoc cref="PackageVersionsQuery.AllowUnsupportedFrameworks"/>
    public bool AllowUnsupportedFrameworks { get; init; }
}
