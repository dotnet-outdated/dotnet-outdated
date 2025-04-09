namespace DotNetOutdated.Core.Services;

public interface INuGetPackageInfoServiceLogger
{
    void PackageSourceSkipped(string sourceName, string packageId);
}