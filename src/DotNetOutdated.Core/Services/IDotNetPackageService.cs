using System;
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
        /// <remarks>
        /// The default implementation keeps existing implementers of this interface working: a plain
        /// minimum version is passed to the <see cref="NuGetVersion"/> overload, and any other range is not supported.
        /// </remarks>
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange) =>
            AddPackage(projectPath, packageName, frameworkName, versionRange, false, false);

        /// <inheritdoc cref="AddPackage(string, string, string, VersionRange)"/>
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange, bool noRestore, bool ignoreFailedSources)
        {
            ArgumentNullException.ThrowIfNull(versionRange);

            return versionRange.IsPlainVersion()
                ? AddPackage(projectPath, packageName, frameworkName, versionRange.MinVersion, noRestore, ignoreFailedSources)
                : throw new NotSupportedException($"{GetType().Name} does not support version ranges ('{versionRange}').");
        }

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
