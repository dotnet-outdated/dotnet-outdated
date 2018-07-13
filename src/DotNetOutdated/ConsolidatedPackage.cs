using System.Collections.Generic;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated
{
    public class ConsolidatedPackage
    {
        public class PackageProjectReference
        {
            public NuGetFramework Framework { get; set; }

            public string Project { get; set; }

            public string Name => $"{Project} [{Framework}]";
        }

        public bool IsAutoReferenced { get; set; }

        public bool IsTransitive { get; set; }

        public NuGetVersion LatestVersion { get; set; }

        public string Name { get; set; }

        public List<PackageProjectReference> Projects { get; set; }

        public NuGetVersion ResolvedVersion { get; set; }

        public string Title
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
    }
}