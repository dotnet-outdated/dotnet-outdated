using NuGet.Versioning;
using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, TimeSpan timeout, bool ignoreFailedSources);

        RunStatus RemovePackage(string projectPath, string packageName, TimeSpan timeout);
    }
}
