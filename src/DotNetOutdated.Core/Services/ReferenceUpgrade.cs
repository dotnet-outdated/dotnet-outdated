using System.Collections.Generic;
using DotNetOutdated.Core.Models;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>An upgrade of one package or SDK reference in one or more projects, made by <see cref="IReferenceUpgrader.Upgrade"/>.</summary>
public sealed record ReferenceUpgrade
{
    /// <summary>The package or SDK id.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether <see cref="Name"/> is a package or an SDK. Set it when known, and always when a project references a
    /// package and an SDK with the same id. When <see langword="null"/> (the default), a name the project declares as
    /// a versioned SDK (<c>Sdk="Id/Version"</c>, <c>&lt;Sdk&gt;</c>, <c>&lt;Import Sdk&gt;</c> or a file-based app's
    /// <c>#:sdk</c> directive) is upgraded as an SDK, and any other name as a package.
    /// </summary>
    public ReferenceKind? Kind { get; init; }

    /// <summary>
    /// The version to upgrade to. Where the reference declares a range with an upper bound that this version satisfies,
    /// such as <c>[6.0.13, 7.0.0)</c>, the upper bound is kept (<c>[6.0.36, 7.0.0)</c>); otherwise the version is
    /// written on its own.
    /// </summary>
    public required NuGetVersion Version { get; init; }

    /// <summary>The projects (or file-based apps) whose reference to upgrade.</summary>
    public required IReadOnlyList<UpgradeProject> Projects
    {
        get => _projects;
        init => _projects = ReadOnlySnapshot.Of(value);
    }

    /// <summary>Don't restore the projects after changing the version.</summary>
    public bool NoRestore { get; init; }

    /// <summary>Treat package sources that fail during restore as warnings.</summary>
    public bool IgnoreFailedSources { get; init; }

    private readonly IReadOnlyList<UpgradeProject> _projects = [];
}

/// <summary>A project whose reference <see cref="IReferenceUpgrader.Upgrade"/> upgrades.</summary>
/// <param name="Path">The project file or file-based app.</param>
/// <param name="TargetFramework">
/// A target framework of the project (short folder name or alias) to upgrade the reference for; when
/// <see langword="null"/>, every target framework. A reference declared per target framework is upgraded only for
/// the target frameworks given, so list a multi-targeted project once for each.
/// </param>
public sealed record UpgradeProject(string Path, string? TargetFramework = null);

/// <summary>What happened to one project's reference.</summary>
public enum UpgradeOutcome
{
    /// <summary>The version was changed, by this upgrade or an earlier one sharing its version location.</summary>
    Upgraded,

    /// <summary>The version location already held the version, so nothing was written or restored.</summary>
    AlreadyCurrent,

    /// <summary>The version couldn't be changed; <see cref="ProjectUpgradeResult.FailureDetails"/> says why.</summary>
    Failed,
}

/// <summary>The result of upgrading a reference in one project.</summary>
public sealed record ProjectUpgradeResult
{
    /// <summary>The project path, as given in <see cref="ReferenceUpgrade.Projects"/>.</summary>
    public required string ProjectPath { get; init; }

    public required UpgradeOutcome Outcome { get; init; }

    /// <summary>Why the upgrade failed; empty unless <see cref="Outcome"/> is <see cref="UpgradeOutcome.Failed"/>.</summary>
    public string FailureDetails { get; init; } = string.Empty;

    /// <summary>Things the user should know about an upgrade that went ahead, such as a version expression replaced by a literal.</summary>
    public IReadOnlyList<string> Warnings { get => _warnings; init => _warnings = ReadOnlySnapshot.Of(value); }

    public bool IsSuccess => Outcome != UpgradeOutcome.Failed;

    private readonly IReadOnlyList<string> _warnings = [];
}
