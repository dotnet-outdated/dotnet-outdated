using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// What MSBuild reports about a project: its restore graph, its evaluated <c>PackageReference</c> items, and where
/// the properties that hold its package versions are defined. Core asks MSBuild questions only through this
/// interface, so an implementation that evaluates projects in-process with <c>Microsoft.Build</c> can replace
/// <see cref="DotNetProjectEvaluator"/> without changing the analysis or upgrade code.
/// </summary>
/// <remarks>
/// Evaluation is read-only. Upgrades edit project files as text so that their formatting is kept, and use the
/// file positions reported here (such as <see cref="PackageVariableInfo.PropertyElementIndex"/>) to find what to edit.
/// Files are read on every call, so results reflect edits made by earlier upgrades.
/// </remarks>
internal interface IProjectEvaluator
{
    /// <summary>
    /// Generates the restore graph of a project, solution, solution filter or file-based app.
    /// </summary>
    /// <exception cref="Exceptions.CommandValidationException">The path could not be evaluated or has nothing to restore.</exception>
    Task<DependencyGraphSpec> GetRestoreGraphAsync(string projectPath, string? runtime);

    /// <summary>
    /// Evaluates a project for one target framework and returns its <c>PackageReference</c> items, including the file
    /// that defined each item.
    /// </summary>
    /// <returns>The evaluated items, or <see langword="null"/> when the project could not be evaluated.</returns>
    IReadOnlyList<PackageReferenceItem>? GetPackageReferenceItems(string projectPath, string targetFramework, string? runtime);

    /// <summary>
    /// The item that sets each package's version for a project file, keyed by package id: where it is written, its
    /// expression and evaluated range, and the version variable behind it. A <c>PackageReference</c> version takes
    /// precedence over a <c>GlobalPackageReference</c>, which takes precedence over a central <c>PackageVersion</c>.
    /// Packages the project doesn't give a version are left out.
    /// </summary>
    /// <param name="targetFramework">
    /// A target framework of the project (short folder name or alias). A package whose version is declared separately
    /// for several target frameworks gets the item declared for this one; when <see langword="null"/>, or the package
    /// has no item for it, any of them.
    /// </param>
    IReadOnlyDictionary<string, PackageVersionDeclaration> GetPackageVersions(string projectPath, string? targetFramework = null);

    /// <summary>
    /// The properties that the <c>Directory.*</c> files MSBuild would import define for a file-based app, keyed by
    /// property name.
    /// </summary>
    IReadOnlyDictionary<string, ImportedProperty> GetImportedProperties(string appPath);
}

/// <summary>A property defined by a file that MSBuild imports for a project, and where its definition in effect is.</summary>
/// <param name="RawValue">The trimmed, unexpanded text of the property element.</param>
/// <param name="FilePath">The full path of the file whose definition is in effect.</param>
/// <param name="ElementIndex">
/// The index of that definition among the same-named property elements under any <c>PropertyGroup</c> in
/// <paramref name="FilePath"/>; -1 when unknown.
/// </param>
/// <param name="Ambiguous">
/// Whether the definition is assigned under a condition that couldn't be evaluated, or derives from such a property,
/// so the value in effect isn't certain.
/// </param>
internal sealed record ImportedProperty(string Name, string RawValue, string FilePath, int ElementIndex, bool Ambiguous);

/// <summary>The item that sets a package's version for a project.</summary>
/// <param name="ElementType"><c>PackageReference</c>, <c>GlobalPackageReference</c> or <c>PackageVersion</c>.</param>
/// <param name="FilePath">The full path of the file the item is written in.</param>
/// <param name="VersionExpression">The unexpanded text of its <c>Version</c> (or <c>VersionOverride</c>).</param>
/// <param name="VersionRange">The evaluated version, or <see langword="null"/> when it couldn't be evaluated statically.</param>
/// <param name="Variable">The version variable behind the version; <see langword="null"/> for a literal version.</param>
/// <param name="IsUpdate">Whether the item is an <c>Update</c> rather than an <c>Include</c>.</param>
internal sealed record PackageVersionDeclaration(
    string ElementType,
    string FilePath,
    string VersionExpression,
    bool IsVersionOverride,
    VersionRange? VersionRange,
    PackageVariableInfo? Variable,
    bool IsUpdate = false)
{
    public const string PackageReference = "PackageReference";
    public const string GlobalPackageReference = "GlobalPackageReference";
    public const string PackageVersion = "PackageVersion";
}
