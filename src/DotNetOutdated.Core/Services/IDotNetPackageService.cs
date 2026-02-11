using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange);

        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange, bool noRestore, bool ignoreFailedSources);

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
