using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    public interface IDotNetAddPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version);
    }
}