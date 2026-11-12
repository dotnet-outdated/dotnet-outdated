using System;
using System.IO.Abstractions;
using System.Linq;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Where a reference's version is written, which decides how it can be upgraded.
/// </summary>
internal abstract record VersionLocation
{
    /// <summary>The range the reference declares at this location, when it can hold one and it could be evaluated.</summary>
    public virtual VersionRange? DeclaredRange => null;
}

/// <summary>
/// A literal version in the project file or a central <c>Directory.Packages.props</c>, which <c>dotnet add package</c>
/// can rewrite, or no version at all (a transitive package), which it adds. <see cref="Declaration"/> is the item that
/// holds the version, if any; <see cref="Warning"/> is set when a version expression is replaced by the literal.
/// </summary>
internal sealed record LiteralVersion(PackageVersionDeclaration? Declaration = null, string? Warning = null) : VersionLocation
{
    public override VersionRange? DeclaredRange => Declaration?.VersionRange;
}

/// <summary>An MSBuild property behind a <c>PackageReference</c>, <c>PackageVersion</c> or <c>GlobalPackageReference</c>.</summary>
/// <param name="DotNetAddCanUpdate">
/// Whether <c>dotnet add package</c> can rewrite the reference (it can't for a <c>GlobalPackageReference</c>, a
/// <c>VersionOverride</c>, or a <c>PackageReference</c> in an imported file), so the property must be written directly.
/// </param>
internal sealed record MSBuildVariableVersion(PackageVariableInfo Variable, bool DotNetAddCanUpdate, VersionRange? Declared) : VersionLocation
{
    public override VersionRange? DeclaredRange => Declared;
}

/// <summary>
/// A literal version on a <c>GlobalPackageReference</c> in <paramref name="FilePath"/>. <c>dotnet add package</c> can't
/// update one (it adds a <c>PackageReference</c> instead, which central package management rejects), so the version
/// must be written directly.
/// </summary>
internal sealed record GlobalPackageReferenceVersion(string FilePath, VersionRange? Declared) : VersionLocation
{
    public override VersionRange? DeclaredRange => Declared;
}

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

    /// <summary>
    /// <paramref name="kind"/> when it is set; otherwise <see cref="ReferenceKind.Sdk"/> for a name the project declares
    /// as a versioned SDK, and <see cref="ReferenceKind.Package"/> for any other name.
    /// </summary>
    public ReferenceKind ResolveKind(string projectPath, string name, ReferenceKind? kind) =>
        kind ?? (projectPath.IsCSharpFile()
            ? FileBasedAppReferenceKind(projectPath, name)
            : ProjectReferenceKind(projectPath, name));

    public VersionLocation Locate(string projectPath, string name, ReferenceKind kind)
    {
        if (projectPath.IsCSharpFile())
        {
            return LocateInFileBasedApp(projectPath, name, kind);
        }

        return kind == ReferenceKind.Sdk
            ? LocateProjectSdk(projectPath, name)
            : LocatePackage(projectPath, name);
    }

    private ReferenceKind ProjectReferenceKind(string projectPath, string name) =>
        ProjectSdkReferenceHelper.Discover(_fileSystem, projectPath)
            .Any(reference => string.Equals(reference.Name, name, StringComparison.OrdinalIgnoreCase))
            ? ReferenceKind.Sdk
            : ReferenceKind.Package;

    private ReferenceKind FileBasedAppReferenceKind(string projectPath, string name) =>
        DeclaresInFileBasedApp(projectPath, name, ReferenceKind.Sdk) ? ReferenceKind.Sdk : ReferenceKind.Package;

    // Whether a file-based app has a #:package or #:sdk directive (literal or through a #:property) for the name.
    private bool DeclaresInFileBasedApp(string projectPath, string name, ReferenceKind kind) =>
        _fileBasedAppDirectives.PackageVariables(projectPath, kind).ContainsKey(name) ||
        _fileBasedAppDirectives.References(projectPath).Any(reference =>
            reference.Kind == kind && string.Equals(reference.Name, name, StringComparison.OrdinalIgnoreCase));

    private VersionLocation LocateInFileBasedApp(string projectPath, string name, ReferenceKind kind)
    {
        if (_fileBasedAppDirectives.PackageVariables(projectPath, kind).TryGetValue(name, out var variableInfo))
        {
            return new FileBasedVariableVersion(variableInfo);
        }

        if (kind == ReferenceKind.Package)
        {
            // `dotnet add package` would add a #:package directive next to the #:sdk one rather than update it.
            if (DeclaresInFileBasedApp(projectPath, name, ReferenceKind.Sdk) &&
                !DeclaresInFileBasedApp(projectPath, name, ReferenceKind.Package))
            {
                return FileBasedAppNotEditable(
                    projectPath,
                    name,
                    $"The file declares {name} with #:sdk, not #:package; upgrade it as an SDK.");
            }

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
        if (!_evaluator.GetPackageVersions(projectPath).TryGetValue(name, out var declaration))
        {
            return new LiteralVersion();
        }

        if (declaration.Variable is not { } variableInfo)
        {
            return declaration.ElementType == PackageVersionDeclaration.GlobalPackageReference
                ? new GlobalPackageReferenceVersion(declaration.FilePath, declaration.VersionRange)
                : new LiteralVersion(declaration);
        }

        bool dotNetAddCanUpdate =
            variableInfo.ElementType != PackageVersionDeclaration.GlobalPackageReference &&
            !variableInfo.IsVersionOverride &&
            !(variableInfo.ElementType == PackageVersionDeclaration.PackageReference &&
              !string.Equals(variableInfo.PackageReferenceFilePath, projectPath, StringComparison.OrdinalIgnoreCase));

        if (variableInfo.UnsupportedReason == null)
        {
            return new MSBuildVariableVersion(variableInfo, dotNetAddCanUpdate, declaration.VersionRange);
        }

        if (!dotNetAddCanUpdate)
        {
            return new NotEditable(
                $"Could not update '{name}' in '{variableInfo.PackageReferenceFilePath}': {variableInfo.UnsupportedReason}. " +
                "Update the version manually.");
        }

        // Treat it like a literal version: the expression is replaced with the new version.
        return new LiteralVersion(
            declaration,
            $"'{name}' in '{variableInfo.PackageReferenceFilePath}' will be set to a literal version because " +
            $"{variableInfo.UnsupportedReason}; its version expression '{variableInfo.PackageReferenceVersion}' is not preserved.");
    }

    private static NotEditable FileBasedAppNotEditable(string projectPath, string name, string hint) =>
        new($"Failed to update file-based app '{projectPath}' for '{name}'. {hint}");
}
