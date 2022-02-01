using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetAddPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, int timeout, bool ignoreFailedSources);
    }
}