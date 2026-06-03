using NuGet.Versioning;

#nullable enable

namespace DotNetOutdated.Core.Services;

/// <summary>
/// A package or SDK reference declared in a .NET file-based app source file via <c>#:package</c> or <c>#:sdk</c>.
/// </summary>
public sealed class FileBasedAppReference
{
    /// <summary>
    /// Gets the NuGet package or SDK package id.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the resolved semantic version from the source file.
    /// </summary>
    public required NuGetVersion ResolvedVersion { get; init; }

    /// <summary>
    /// Gets the version range used when resolving the latest version on NuGet feeds. The resolved version is the
    /// inclusive floor with an open upper bound so newer versions can be discovered.
    /// </summary>
    public required VersionRange VersionRange { get; init; }

    /// <summary>
    /// Gets whether the reference came from a <c>#:package</c> or <c>#:sdk</c> directive.
    /// </summary>
    public FileBasedAppReferenceKind Kind { get; init; }

    /// <summary>
    /// Gets the unresolved package or SDK id expression from the source directive (for example <c>Cake.Sdk</c> or <c>$(SdkId)</c>).
    /// </summary>
    public string NameExpression { get; init; } = string.Empty;

    /// <summary>
    /// Gets the unresolved version expression from the source directive (for example <c>6.0.0</c> or <c>$(SdkVersion)</c>).
    /// </summary>
    public string VersionExpression { get; init; } = string.Empty;

    /// <summary>
    /// Gets variable-tracking metadata when the directive uses a <c>#:property</c>-backed version; otherwise <see langword="null"/>.
    /// </summary>
    public PackageVariableInfo? VariableInfo { get; init; }

    /// <summary>
    /// Gets whether either directive expression contains an MSBuild property reference such as <c>$(PropertyName)</c>.
    /// </summary>
    public bool UsesPropertyReferences =>
        FileBasedAppReferenceHelper.ContainsPropertyReference(NameExpression, VersionExpression);
}

/// <summary>
/// Helpers for file-based app directive expressions.
/// </summary>
internal static class FileBasedAppReferenceHelper
{
    /// <summary>
    /// Determines whether either expression contains an MSBuild property reference.
    /// </summary>
    public static bool ContainsPropertyReference(string nameExpression, string versionExpression) =>
        ExpressionContainsPropertyReference(nameExpression) ||
        ExpressionContainsPropertyReference(versionExpression);

    /// <summary>
    /// Determines whether a single expression contains an MSBuild property reference.
    /// </summary>
    public static bool ExpressionContainsPropertyReference(string expression) =>
        !string.IsNullOrEmpty(expression) && expression.Contains("$(");

    /// <summary>
    /// Creates a minimum-inclusive version range (<c>[version, )</c>) for a directive version. The directive
    /// version is the current floor, but the range is left open above it so newer versions can be discovered.
    /// </summary>
    public static VersionRange CreateMinimumVersionRange(NuGetVersion version) =>
        new(version, includeMinVersion: true, maxVersion: null, includeMaxVersion: false);

    /// <summary>
    /// Gets the dictionary key for storing a file-based app reference in <see cref="Models.TargetFramework.Dependencies"/>.
    /// Package and SDK directives with the same id use distinct keys.
    /// </summary>
    public static string GetDependencyDictionaryKey(FileBasedAppReference reference) =>
        GetDependencyDictionaryKey(reference.Name, reference.Kind);

    /// <summary>
    /// Gets the dictionary key for storing a file-based app reference in <see cref="Models.TargetFramework.Dependencies"/>.
    /// </summary>
    public static string GetDependencyDictionaryKey(string name, FileBasedAppReferenceKind kind) =>
        kind switch
        {
            FileBasedAppReferenceKind.Sdk => $"{name}#sdk",
            FileBasedAppReferenceKind.Package => $"{name}#package",
            _ => name
        };
}

/// <summary>
/// The kind of file-based app directive that declared a reference.
/// </summary>
public enum FileBasedAppReferenceKind
{
    /// <summary>
    /// A <c>#:package</c> directive.
    /// </summary>
    Package,

    /// <summary>
    /// A <c>#:sdk</c> directive.
    /// </summary>
    Sdk
}
