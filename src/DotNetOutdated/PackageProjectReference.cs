using NuGet.Frameworks;

namespace DotNetOutdated
{
    public sealed class PackageProjectReference(string projectFilePath)
    {
        public string Description => $"{Project} [{Framework}]";

        public NuGetFramework? Framework { get; set; }

        public string? Project { get; set; }

        public string ProjectFilePath { get; set; } = projectFilePath;
    }
}
