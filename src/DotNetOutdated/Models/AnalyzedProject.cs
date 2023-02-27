using System.Collections.Generic;
using System.Linq;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Models
{
    [JsonObject(MemberSerialization.OptIn)]
    public class AnalyzedProject
    {
        [JsonProperty(Order = 2)]
        public IReadOnlyList<AnalyzedTargetFramework> TargetFrameworks { get; }

        [JsonProperty(Order = 0)]
        public string Name { get; set; }

        [JsonProperty(Order = 1)]
        public string FilePath { get; set; }

        public AnalyzedProject(string name, string filePath, IEnumerable<AnalyzedTargetFramework> targetFrameworks)
        {
            Name = name;
            FilePath = filePath;
            TargetFrameworks = targetFrameworks.OrderBy(p => p.Name.Framework).ToList();
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class AnalyzedTargetFramework
    {
        [JsonProperty(Order = 1)]
        public IReadOnlyList<AnalyzedDependency> Dependencies { get; }

        [JsonProperty(Order = 0)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetFramework Name { get; set; }

        public AnalyzedTargetFramework(NuGetFramework name, IEnumerable<AnalyzedDependency> dependencies)
        {
            Name = name;
            Dependencies = dependencies.OrderBy(p => p.Name).ToList();
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class AnalyzedDependency
    {
        private readonly Dependency _dependency;

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

        public bool IsAutoReferenced => _dependency.IsAutoReferenced;

        public bool IsTransitive => _dependency.IsTransitive;

        public bool IsVersionCentrallyManaged => _dependency.IsVersionCentrallyManaged;

        [JsonProperty(Order = 0)]
        public string Name => _dependency.Name;

        [JsonProperty(Order = 1)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion ResolvedVersion => _dependency.ResolvedVersion;

        [JsonProperty(Order = 2)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion LatestVersion { get; set; }

        [JsonProperty(Order = 3)]
        [JsonConverter(typeof(StringEnumConverter))]
        public DependencyUpgradeSeverity UpgradeSeverity
        {
            get
            {
                if (LatestVersion == null || ResolvedVersion == null)
                    return DependencyUpgradeSeverity.Unknown;

                if (LatestVersion.Major > ResolvedVersion.Major || ResolvedVersion.IsPrerelease)
                    return DependencyUpgradeSeverity.Major;
                if (LatestVersion.Minor > ResolvedVersion.Minor)
                    return DependencyUpgradeSeverity.Minor;
                if (LatestVersion.Patch > ResolvedVersion.Patch || LatestVersion.Revision > ResolvedVersion.Revision)
                    return DependencyUpgradeSeverity.Patch;

                return DependencyUpgradeSeverity.None;
            }
        }

        public AnalyzedDependency(Dependency dependency)
        {
            _dependency = dependency;
        }

        public AnalyzedDependency(Dependency dependency, NuGetVersion latestVersion) : this(dependency)
        {
            LatestVersion = latestVersion;
        }
    }
}
