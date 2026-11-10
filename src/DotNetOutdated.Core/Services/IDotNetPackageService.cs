using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        /// <summary>
        /// Sets the version of a project's package or SDK reference to <paramref name="versionRange"/> (which must have
        /// a lower bound, e.g. <c>13.0.4</c> or <c>[13.0.4, 14.0.0)</c>) wherever that version is written: the project,
        /// <c>Directory.Packages.props</c>, an MSBuild property, an <c>Sdk</c> declaration, or a file-based app directive.
        /// Property references and the files' formatting are kept. Versions that can only hold a single version
        /// (MSBuild SDKs, file-based app directives) receive the range's lower bound.
        /// </summary>
        /// <param name="frameworkName">The target framework (short folder name or alias) to update the reference for.</param>
        /// <param name="kind">Whether <paramref name="packageName"/> is a package or an SDK, which matters when a project references both.</param>
        RunStatus AddPackage(
            string projectPath,
            string packageName,
            string frameworkName,
            VersionRange versionRange,
            bool noRestore = false,
            bool ignoreFailedSources = false,
            ReferenceKind kind = ReferenceKind.Package);

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
