using System;
using System.IO.Abstractions;
using System.Linq;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Where a reference's version is written, which decides how it can be upgraded.
/// </summary>
internal abstract record VersionLocation;

/// <summary>
/// A literal version in the project file or a central <c>Directory.Packages.props</c>, which <c>dotnet add package</c>
/// can rewrite. <see cref="Warning"/> is set when a version expression is replaced by the literal.
/// </summary>
internal sealed record LiteralVersion(string? Warning = null) : VersionLocation;

/// <summary>An MSBuild property behind a <c>PackageReference</c>, <c>PackageVersion</c> or <c>GlobalPackageReference</c>.</summary>
/// <param name="DotNetAddCanUpdate">
/// Whether <c>dotnet add package</c> can rewrite the reference (it can't for a <c>GlobalPackageReference</c>, a
/// <c>VersionOverride</c>, or a <c>PackageReference</c> in an imported file), so the property must be written directly.
/// </param>
internal sealed record MSBuildVariableVersion(PackageVariableInfo Variable, bool DotNetAddCanUpdate) : VersionLocation;

/// <summary>A versioned MSBuild project SDK declared in the project file.</summary>
internal sealed record ProjectSdkVersion(ProjectSdkReference Sdk) : VersionLocation;

/// <summary>A literal <c>#:sdk Id@Version</c> directive in a file-based app.</summary>
internal sealed record FileBasedDirectiveVersion(FileBasedAppReference Reference) : VersionLocation;

/// <summary>A property behind a file-based app's <c>#:package</c> or <c>#:sdk</c> directive.</summary>
internal sealed record FileBasedVariableVersion(PackageVariableInfo Variable) : VersionLocation;

/// <summary>A version that can't be changed automatically; <see cref="Message"/> says why.</summary>
internal sealed record NotEditable(string Message) : VersionLocation;

/// <summary>
/// Finds where the version of a package or SDK referenced by a project is written: in the project, a Directory.*
/// file it imports, an MSBuild property, its <c>Sdk</c> declarations, or the directives of a file-based app.
/// Files are read on every call, so the answer reflects edits made by earlier upgrades.
/// </summary>
internal sealed class VersionLocator(IFileSystem fileSystem, IProjectEvaluator evaluator, FileBasedAppDirectives fileBasedAppDirectives)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProjectEvaluator _evaluator = evaluator;
    private readonly FileBasedAppDirectives _fileBasedAppDirectives = fileBasedAppDirectives;

    public VersionLocation Locate(string projectPath, string name, ReferenceKind kind) =>
        projectPath.IsCSharpFile()
            ? LocateInFileBasedApp(projectPath, name, kind)
            : kind == ReferenceKind.Sdk
                ? LocateProjectSdk(projectPath, name)
                : LocatePackage(projectPath, name);

    private VersionLocation LocateInFileBasedApp(string projectPath, string name, ReferenceKind kind)
    {
        if (_fileBasedAppDirectives.PackageVariables(projectPath, kind).TryGetValue(name, out var variableInfo))
        {
            return new FileBasedVariableVersion(variableInfo);
        }

        if (kind == ReferenceKind.Package)
        {
            // `dotnet add package` rewrites a literal #:package directive.
            return new LiteralVersion();
        }

        var reference = _fileBasedAppDirectives.References(projectPath)
            .FirstOrDefault(reference =>
                reference.Kind == ReferenceKind.Sdk &&
                reference.VariableInfo == null &&
                string.Equals(reference.Name, name, StringComparison.OrdinalIgnoreCase));

        if (reference == null)
        {
            return FileBasedAppNotEditable(
                projectPath,
                name,
                $"Expected a literal #:sdk {name}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
        }

        if (reference.UsesPropertyReferences)
        {
            return FileBasedAppNotEditable(
                projectPath,
                name,
                "Direct #:sdk updates require a literal id@version (for example #:sdk Cake.Sdk@6.0.0); " +
                "property references in the name or version must be changed via #:property lines.");
        }

        return new FileBasedDirectiveVersion(reference);
    }

    private VersionLocation LocateProjectSdk(string projectPath, string name)
    {
        var sdk = ProjectSdkReferenceHelper.Discover(_fileSystem, projectPath)
            .FirstOrDefault(reference => string.Equals(reference.Name, name, StringComparison.OrdinalIgnoreCase));

        return sdk != null
            ? new ProjectSdkVersion(sdk)
            : new NotEditable(
                $"Failed to update the version of SDK '{name}' in '{projectPath}': the project file declares no versioned SDK with that name.");
    }

    private VersionLocation LocatePackage(string projectPath, string name)
    {
        if (!_evaluator.GetVersionVariables(projectPath).TryGetValue(name, out var variableInfo))
        {
            return new LiteralVersion();
        }

        bool dotNetAddCanUpdate =
            variableInfo.ElementType != "GlobalPackageReference" &&
            !variableInfo.IsVersionOverride &&
            !(variableInfo.ElementType == "PackageReference" &&
              !string.Equals(variableInfo.PackageReferenceFilePath, projectPath, StringComparison.OrdinalIgnoreCase));

        if (variableInfo.UnsupportedReason == null)
        {
            return new MSBuildVariableVersion(variableInfo, dotNetAddCanUpdate);
        }

        if (!dotNetAddCanUpdate)
        {
            return new NotEditable(
                $"Could not update '{name}' in '{variableInfo.PackageReferenceFilePath}': {variableInfo.UnsupportedReason}. " +
                "Update the version manually.");
        }

        // Treat it like a literal version: the expression is replaced with the new version.
        return new LiteralVersion(
            $"'{name}' in '{variableInfo.PackageReferenceFilePath}' will be set to a literal version because " +
            $"{variableInfo.UnsupportedReason}; its version expression '{variableInfo.PackageReferenceVersion}' is not preserved.");
    }

    private static NotEditable FileBasedAppNotEditable(string projectPath, string name, string hint) =>
        new($"Failed to update file-based app '{projectPath}' for '{name}'. {hint}");
}
