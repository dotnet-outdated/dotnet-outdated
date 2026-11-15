using System;
using System.Collections.Generic;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// A package version and target framework to check with
/// <see cref="INuGetPackageInfoService.DeclaresTargetFrameworkUnsupportedAsync"/>.
/// </summary>
public sealed record TargetFrameworkSupportQuery
{
    /// <summary>The package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>The package version.</summary>
    public required NuGetVersion Version { get; init; }

    /// <summary>The target framework the version would be used for.</summary>
    public required NuGetFramework TargetFramework { get; init; }

    /// <summary>The package sources to download the package from.</summary>
    public required IReadOnlyList<Uri> Sources { get; init; }

    /// <summary>
    /// The project whose NuGet settings (source credentials and package source mapping) apply; <see langword="null"/>
    /// for the user's and machine's settings only.
    /// </summary>
    public string? ProjectFilePath { get; init; }
}
