using NuGet.Versioning;
using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetAddPackageService
    {
        RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, TimeSpan Timeout, bool ignoreFailedSources);
    }
}
