using NuGet.Frameworks;
using NuGet.Versioning;
using System;
using System.Collections.Generic;

namespace DotNetOutdated.Core.Models
{
    public class Project
    {
        public string FilePath { get; }

        public string Name { get; }

        public IList<Uri> Sources { get; }

        public IList<TargetFramework> TargetFrameworks { get; } = new List<TargetFramework>();

        public NuGetVersion Version { get; }

        public Project(string name, string filePath, IEnumerable<Uri> sources, NuGetVersion version)
        {
            FilePath = filePath;
            Name = name;
            Sources = new List<Uri>(sources);
            Version = version;
        }
    }

    public class TargetFramework
    {
        public Dictionary<string, Dependency> Dependencies { get; } = [];

        public NuGetFramework Name { get; set; }

        public TargetFramework(NuGetFramework name)
        {
            Name = name;
        }
    }

    public class Dependency
    {
        public bool IsAutoReferenced { get; }

        public bool IsDevelopmentDependency { get; }

        public bool IsTransitive { get; }

        public string Name { get; }

        public NuGetVersion ResolvedVersion { get; }

        public VersionRange VersionRange { get; }

        public Dependency(string name, VersionRange versionRange, NuGetVersion resolvedVersion, bool isAutoReferenced, bool isTransitive, bool isDevelopmentDependency)
        {
            Name = name;
            VersionRange = versionRange;
            ResolvedVersion = resolvedVersion;
            IsAutoReferenced = isAutoReferenced;
            IsTransitive = isTransitive;
            IsDevelopmentDependency = isDevelopmentDependency;
        }
    }
}
