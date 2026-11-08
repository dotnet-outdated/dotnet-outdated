using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version);

        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources);

        /// <summary>
        /// Adds or updates a package reference using a version range, e.g. <c>[13.0.4, 14.0.0)</c>.
        /// The range must have a lower bound.
        /// </summary>
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange);

        /// <inheritdoc cref="AddPackage(string, string, string, VersionRange)"/>
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange, bool noRestore, bool ignoreFailedSources);

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
