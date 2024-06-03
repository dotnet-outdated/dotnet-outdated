using System.Collections.Generic;
using System.Linq;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Services;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Models
{
    public class AnalyzedProject
    {
        [JsonPropertyOrder(2)]
        public IReadOnlyList<AnalyzedTargetFramework> TargetFrameworks { get; }

        [JsonPropertyOrder(0)]
        public string Name { get; set; }

        [JsonPropertyOrder(1)]
        public string FilePath { get; set; }

        public AnalyzedProject(string name, string filePath, IEnumerable<AnalyzedTargetFramework> targetFrameworks)
        {
            Name = name;
            FilePath = filePath;
            TargetFrameworks = targetFrameworks.OrderBy(p => p.Name.Framework).ToList();
        }
    }

    public class AnalyzedTargetFramework
    {
        [JsonPropertyOrder(1)]
        public IReadOnlyList<AnalyzedDependency> Dependencies { get; }

        [JsonPropertyOrder(0)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetFramework Name { get; set; }

        public AnalyzedTargetFramework(NuGetFramework name, IEnumerable<AnalyzedDependency> dependencies)
        {
            Name = name;
            Dependencies = dependencies.OrderBy(p => p.Name).ToList();
        }
    }

    public class AnalyzedDependency
    {
        private static readonly NuGetVersion Min = new(0, 0, 0);

        private static readonly NuGetVersion Max = new(int.MaxValue, int.MaxValue, int.MaxValue);

        private readonly Dependency _dependency;

        [JsonIgnore]
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

        [JsonIgnore]
        public bool IsAutoReferenced => _dependency.IsAutoReferenced;

        [JsonIgnore]
        public bool IsTransitive => _dependency.IsTransitive;

        [JsonIgnore]
        public bool IsVersionCentrallyManaged => _dependency.IsVersionCentrallyManaged;

        [JsonPropertyOrder(0)]
        public string Name => _dependency.Name;

        [JsonPropertyOrder(1)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion? ResolvedVersion => _dependency.ResolvedVersion;

        [JsonPropertyOrder(2)]
        [JsonConverter(typeof(ToStringJsonConverter))]
        public NuGetVersion? LatestVersion { get; set; }

        [JsonIgnore]
        public NuGetVersion LatestVersionOrDefault => LatestVersion ?? Max;

        [JsonIgnore]
        public NuGetVersion ResolvedVersionOrDefault => ResolvedVersion ?? Min;

        [JsonPropertyOrder(3)]
        [JsonConverter(typeof(JsonStringEnumConverter))]
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

        public AnalyzedDependency(Dependency dependency, NuGetVersion? latestVersion) : this(dependency)
        {
            LatestVersion = latestVersion;
        }
    }

    public class CsvDependency(string projectName, string targetFrameworkName, string dependencyName)
    {
        public string ProjectName { get; set; } = projectName;
        public string TargetFrameworkName { get; set; } = targetFrameworkName;
        public string DependencyName { get; set; } = dependencyName;
        public string? ResolvedVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? UpgradeSeverity { get; set; }
    }
}