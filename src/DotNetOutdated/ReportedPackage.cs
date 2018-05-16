using NuGet.Versioning;

namespace DotNetOutdated
{
    public class ReportedPackage
    {
        public NuGetVersion LatestVersion { get; }

        public string Name { get; }

        public NuGetVersion ReferencedVersion { get; }

        public ReportedPackage(string name, NuGetVersion referencedVersion, NuGetVersion latestVersion)
        {
            LatestVersion = latestVersion;
            Name = name;
            ReferencedVersion = referencedVersion;
        }

    }
}