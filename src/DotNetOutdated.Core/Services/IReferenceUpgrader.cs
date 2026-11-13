using System.Collections.Generic;

namespace DotNetOutdated.Core.Services;

/// <summary>Upgrades package and SDK references.</summary>
public interface IReferenceUpgrader
{
    /// <summary>
    /// Sets the version of a reference in each of <see cref="ReferenceUpgrade.Projects"/> wherever that version is
    /// written: the project, <c>Directory.Packages.props</c>, an MSBuild property, an <c>Sdk</c> declaration, or a
    /// file-based app directive. Version variables, the files' formatting, and a declared upper bound that the new
    /// version still satisfies are kept. A location shared by several projects is written once, and each project is
    /// restored once after it changes (unless <see cref="ReferenceUpgrade.NoRestore"/> is set).
    /// </summary>
    /// <returns>
    /// One result per project, in the order given, with a project listed more than once reported once, followed by
    /// one for each project of <see cref="FindHeldBackSharingVersion"/> not listed already. A project that fails
    /// doesn't stop the others.
    /// </returns>
    IReadOnlyList<ProjectUpgradeResult> Upgrade(ReferenceUpgrade upgrade);

    /// <summary>
    /// The projects in <see cref="ReferenceUpgrade.HeldBack"/> whose package version is written where that of a
    /// project in <see cref="ReferenceUpgrade.Projects"/> is (an unconditional reference, a central
    /// <c>PackageVersion</c>, a shared MSBuild property), so <see cref="Upgrade"/> upgrades and restores them too.
    /// </summary>
    IReadOnlyList<UpgradeProject> FindHeldBackSharingVersion(ReferenceUpgrade upgrade);
}
