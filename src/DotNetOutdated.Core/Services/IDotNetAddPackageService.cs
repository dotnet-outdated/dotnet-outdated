using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetAddPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version);

        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources);
    }
}
