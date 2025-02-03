using DotNetOutdated.Core.Services;
using McMaster.Extensions.CommandLineUtils;

namespace DotNetOutdated;

internal sealed class NuGetPackageInfoServiceLogger(IConsole console) : INuGetPackageInfoServiceLogger
{
    public void PackageSourceSkipped(string sourceName, string packageId)
    {
        console.WriteLine(
            $"Package source {sourceName} skipped by packageSourceMapping, packageId: {packageId}");
    }
}