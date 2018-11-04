using System;
using System.Collections.Generic;
using System.Linq;
using DotNetOutdated.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Models
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Project
    {
        public List<Uri> Sources { get; set; } = new List<Uri>();
        
        [JsonProperty(Order=2)]
        public List<TargetFramework> TargetFrameworks { get; set; } = new List<TargetFramework>();

        [JsonProperty(Order=0)]
        public string Name { get; set; }

        [JsonProperty(Order=1)]
        public string FilePath { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class TargetFramework
    {
        [JsonProperty(Order=1)]
        public List<Dependency> Dependencies { get; set; } = new List<Dependency>();


        [JsonProperty(Order=0)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetFramework Name { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
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

        public bool HasError => !String.IsNullOrEmpty(Error);

        public bool IsAutoReferenced { get; set; }

        public bool IsDevelopmentDependency { get; set; }

        public bool IsTransitive { get; set; }

        [JsonProperty(Order=0)]
        public string Name { get; set; }
            
        public VersionRange VersionRange { get; set; }

        [JsonProperty(Order=1)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion ResolvedVersion { get; set; }

        [JsonProperty(Order=2)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion LatestVersion { get; set; }

        [JsonProperty(Order=3)]
        [JsonConverter(typeof(StringEnumConverter))]
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
}