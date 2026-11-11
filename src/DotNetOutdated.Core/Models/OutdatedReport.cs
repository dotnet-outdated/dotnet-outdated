using System.Collections.Generic;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Models
{
    /// <summary>How big the change from a dependency's resolved version to its latest version is.</summary>
    public enum DependencyUpgradeSeverity
    {
        None,
        Patch,
        Minor,
        Major,
        Unknown
    }

    /// <summary>A project with the dependencies of each target framework that have a newer version.</summary>
    public sealed record OutdatedProject
    {
        public required string Name { get; init; }

        public required string FilePath { get; init; }

        /// <summary>The target frameworks with at least one reported dependency, ordered by framework.</summary>
        public required IReadOnlyList<OutdatedTargetFramework> TargetFrameworks
        {
            get => _targetFrameworks;
            init => _targetFrameworks = ReadOnlySnapshot.Of(value);
        }

        private readonly IReadOnlyList<OutdatedTargetFramework> _targetFrameworks = [];
    }

    /// <summary>A target framework of a project and its reported dependencies.</summary>
    public sealed record OutdatedTargetFramework
    {
        public required NuGetFramework Name { get; init; }

        /// <summary>The alias the project uses for the framework in <c>TargetFrameworks</c>, if any.</summary>
        public string? TargetAlias { get; init; }

        /// <summary>The reported dependencies, ordered by name.</summary>
        public required IReadOnlyList<OutdatedDependency> Dependencies
        {
            get => _dependencies;
            init => _dependencies = ReadOnlySnapshot.Of(value);
        }

        private readonly IReadOnlyList<OutdatedDependency> _dependencies = [];
    }

    /// <summary>A dependency and the newest version it may move to.</summary>
    public sealed record OutdatedDependency
    {
        public required Dependency Dependency { get; init; }

        /// <summary>
        /// The newest allowed version; <see langword="null"/> when it couldn't be determined, for example because the
        /// package isn't resolved or no version is older than the requested number of days.
        /// </summary>
        public NuGetVersion? LatestVersion { get; init; }

        /// <summary>How big the change from <see cref="Dependency.ResolvedVersion"/> to <see cref="LatestVersion"/> is.</summary>
        public DependencyUpgradeSeverity UpgradeSeverity => GetUpgradeSeverity(Dependency.ResolvedVersion, LatestVersion);

        /// <summary>How big the change from <paramref name="resolvedVersion"/> to <paramref name="latestVersion"/> is.</summary>
        public static DependencyUpgradeSeverity GetUpgradeSeverity(NuGetVersion? resolvedVersion, NuGetVersion? latestVersion)
        {
            if (latestVersion == null || resolvedVersion == null)
                return DependencyUpgradeSeverity.Unknown;

            if (latestVersion.Major > resolvedVersion.Major || resolvedVersion.IsPrerelease)
                return DependencyUpgradeSeverity.Major;
            if (latestVersion.Minor > resolvedVersion.Minor)
                return DependencyUpgradeSeverity.Minor;
            if (latestVersion.Patch > resolvedVersion.Patch || latestVersion.Revision > resolvedVersion.Revision)
                return DependencyUpgradeSeverity.Patch;

            return DependencyUpgradeSeverity.None;
        }
    }
}
