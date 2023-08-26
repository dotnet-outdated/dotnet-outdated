using NuGet.Versioning;
using System;

namespace DotNetOutdated.Core.Services
{
    public interface ICentralPackageVersionManagementService
    {
        RunStatus AddPackage(string projectFilePath, string packageName, NuGetVersion version, bool noRestore, TimeSpan Timeout);
    }
}