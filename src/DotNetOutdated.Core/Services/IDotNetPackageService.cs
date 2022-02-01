using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources, int timeout);

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
