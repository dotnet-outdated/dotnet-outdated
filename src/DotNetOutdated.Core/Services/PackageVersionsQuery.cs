using System;
using System.Collections.Generic;
using NuGet.Frameworks;

namespace DotNetOutdated.Core.Services;

/// <summary>Which versions of a package to list with <see cref="INuGetPackageInfoService.GetAllVersionsAsync"/>.</summary>
public sealed record PackageVersionsQuery
{
    /// <summary>The package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>The package sources to search.</summary>
    public required IReadOnlyList<Uri> Sources { get; init; }

    /// <summary>
    /// The target framework the package must support. Versions with no dependency group compatible with it are left
    /// out, unless <see cref="IsDevelopmentDependency"/> is set.
    /// </summary>
    public required NuGetFramework TargetFramework { get; init; }

    /// <summary>
    /// The project whose NuGet settings (source credentials and package source mapping) apply; <see langword="null"/>
    /// for the user's and machine's settings only.
    /// </summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>Whether to include prerelease versions.</summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>Whether the package is a development dependency, which skips the target framework check.</summary>
    public bool IsDevelopmentDependency { get; init; }

    /// <summary>Leave out versions published fewer than this many days ago; 0 to include every version.</summary>
    public int OlderThanDays { get; init; }

    /// <summary>Treat package sources that fail as having no versions rather than failing.</summary>
    public bool IgnoreFailedSources { get; init; }

    /// <summary>
    /// Keep versions that reach a .NET <see cref="TargetFramework"/> only through a netstandard fallback while declaring
    /// it unsupported with a build warning, as the .NET runtime packages do for .NET versions they no longer support.
    /// </summary>
    public bool AllowUnsupportedFrameworks { get; init; }
}
