using NuGet.Versioning;

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
    public NuGetVersion ResolvedVersion { get; init; }

    /// <summary>
    /// Gets the version range used when resolving the latest version on NuGet feeds.
    /// </summary>
    public VersionRange VersionRange { get; init; }

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
    public PackageVariableInfo VariableInfo { get; init; }

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
