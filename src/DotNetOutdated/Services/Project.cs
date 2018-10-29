using System;
using System.Collections.Generic;
using NuGet.Versioning;
using NuGet.Frameworks;

namespace DotNetOutdated.Services
{
    public enum DependencyUpgradeSeverity
    {
        None,
        Patch,
        Minor,
        Major
    }

    public class Project
    {
        public class Dependency
        {
            public string Description
            {
                get
                {
                    string description = Name;

                    if (IsAutoReferenced)
                        description += " [A]";
                    else if (IsTransitive)
                        description += " [T]";

                    return description;
                }
            }

            public string Error { get; set; }

            public bool HasError => !string.IsNullOrEmpty(Error);

            public bool IsAutoReferenced { get; set; }

            public bool IsTransitive { get; set; }

            public string Name { get; set; }
            
            public VersionRange VersionRange { get; set; }

            public NuGetVersion ResolvedVersion { get; set; }

            public NuGetVersion LatestVersion { get; set; }
            public DependencyUpgradeSeverity? UpgradeSeverity
            {
                get
                {
                    if (LatestVersion == null || ResolvedVersion == null)
                        return null;

                    if (LatestVersion.Major > ResolvedVersion.Major || ResolvedVersion.IsPrerelease)
                        return DependencyUpgradeSeverity.Major;
                    if (LatestVersion.Minor > ResolvedVersion.Minor)
                        return DependencyUpgradeSeverity.Minor;
                    if (LatestVersion.Patch > ResolvedVersion.Patch || LatestVersion.Revision > ResolvedVersion.Revision)
                        return DependencyUpgradeSeverity.Patch;
                    
                    return DependencyUpgradeSeverity.None;
                }
            }

        }

        public class TargetFramework
        {
            public List<Dependency> Dependencies { get; set; } = new List<Dependency>();

            public NuGetFramework Name { get; set; }
        }

        public List<Uri> Sources { get; set; } = new List<Uri>();
        
        public List<TargetFramework> TargetFrameworks { get; set; } = new List<TargetFramework>();

        public string Name { get; set; }

        public string FilePath { get; set; }
    }

}