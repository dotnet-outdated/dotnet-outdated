using System;
using System.Collections.Generic;
using NuGet.Versioning;
using NuGet.Frameworks;

namespace DotNetOutdated.Services
{
    public class Project
    {
        public class Dependency
        {
            public string Name { get; set; }

            public VersionRange VersionRange { get; set; }
        }

        public class TargetFramework
        {
            public List<Dependency> Dependencies { get; set; } = new List<Dependency>();

            public NuGetFramework Name { get; set; }
        }

        public List<Dependency> Dependencies { get; set; } = new List<Dependency>();

        public List<Uri> Sources { get; set; } = new List<Uri>();
        
        public List<TargetFramework> TargetFrameworks { get; set; } = new List<TargetFramework>();

        public string Name { get; set; }
    }

}