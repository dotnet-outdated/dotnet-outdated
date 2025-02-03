using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

public interface ICentralPackageVersionManagementService
{
    RunStatus AddPackage(string projectFilePath, string packageName, NuGetVersion version, bool noRestore);
}