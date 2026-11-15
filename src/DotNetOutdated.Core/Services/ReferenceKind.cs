namespace DotNetOutdated.Core.Services;

/// <summary>
/// What kind of reference brings a package into a project. A project can reference a package and an SDK with the
/// same id, so the kind is part of a reference's identity.
/// </summary>
public enum ReferenceKind
{
    /// <summary>
    /// A NuGet package: a <c>PackageReference</c> (with its version possibly in <c>Directory.Packages.props</c>),
    /// a <c>GlobalPackageReference</c>, or a file-based app's <c>#:package</c> directive.
    /// </summary>
    Package,

    /// <summary>
    /// An MSBuild project SDK with a version: <c>&lt;Project Sdk="Id/Version"&gt;</c>, <c>&lt;Sdk&gt;</c>,
    /// <c>&lt;Import Sdk&gt;</c>, or a file-based app's <c>#:sdk</c> directive.
    /// </summary>
    Sdk
}
