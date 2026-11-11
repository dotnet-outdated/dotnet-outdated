using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>A change to the version of one package or SDK reference, made by <see cref="IDotNetPackageService.Upgrade"/>.</summary>
public sealed record ReferenceUpgrade
{
    /// <summary>The project file or file-based app that declares the reference.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>The package or SDK id.</summary>
    public required string Name { get; init; }

    /// <summary>Whether <see cref="Name"/> is a package or an SDK, which matters when a project references both.</summary>
    public ReferenceKind Kind { get; init; } = ReferenceKind.Package;

    /// <summary>The target framework (short folder name or alias) to upgrade the reference for.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>
    /// The new version. It must have a lower bound, for example <c>13.0.4</c> or <c>[13.0.4, 14.0.0)</c>. Locations
    /// that can only hold a single version (MSBuild SDKs, file-based app directives) receive the lower bound.
    /// </summary>
    public required VersionRange Version { get; init; }

    /// <summary>Don't restore the project after changing the version.</summary>
    public bool NoRestore { get; init; }

    /// <summary>Treat package sources that fail during restore as warnings.</summary>
    public bool IgnoreFailedSources { get; init; }
}
