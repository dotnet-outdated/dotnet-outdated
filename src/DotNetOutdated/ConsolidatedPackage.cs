using DotNetOutdated.Models;
using NuGet.Versioning;
using System.Collections.Generic;

namespace DotNetOutdated
{
    public class ConsolidatedPackage
    {
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

        public List<PackageProjectReference> Projects { get; set; } = new();

        public NuGetVersion ResolvedVersion { get; set; }

        public DependencyUpgradeSeverity? UpgradeSeverity { get; set; }
    }
}
