using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated
{
    public sealed class PackageProjectReference
    {
        public string Description => $"{Project} [{Framework}]";

        public NuGetFramework Framework { get; set; }

        public string Project { get; set; }

        public string ProjectFilePath { get; set; }

        public VersionRange OriginalVersionRange { get; set; }
    }
}
