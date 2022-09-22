using System.Collections.Generic;
using DotNetOutdated.Models;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated
{
    public class ConsolidatedPackage
    {
        public class PackageProjectReference
        {
            public string Description => $"{Project} [{Framework}]";

            public NuGetFramework Framework { get; set; }

            public string Project { get; set; }

            public string ProjectFilePath { get; set; }
        }

        public string Description
        {
            get
            {
                string title = Name;

                if (IsAutoReferenced)
                    title += " [A]";
                else if (IsTransitive)
                    title += " [T]";

                return title;
            }
        }

        public bool IsAutoReferenced { get; set; }

        public bool IsTransitive { get; set; }

        public bool IsVersionCentrallyManaged { get; set; }

        public NuGetVersion LatestVersion { get; set; }

        public string Name { get; set; }

        public List<PackageProjectReference> Projects { get; set; }

        public NuGetVersion ResolvedVersion { get; set; }

        public DependencyUpgradeSeverity? UpgradeSeverity { get; set; }
    }
}