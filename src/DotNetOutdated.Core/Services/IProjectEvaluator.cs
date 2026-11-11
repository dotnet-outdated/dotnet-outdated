using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.ProjectModel;

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
    /// The version variable behind each package version of a project file, keyed by package id. Packages whose version
    /// is a literal are left out. A <c>PackageReference</c> version takes precedence over a central version.
    /// </summary>
    IReadOnlyDictionary<string, PackageVariableInfo> GetVersionVariables(string projectPath);

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
