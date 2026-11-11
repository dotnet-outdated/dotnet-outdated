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

    /// <summary>Why a dependency's latest version couldn't be determined.</summary>
    public enum LatestVersionUnknownReason
    {
        /// <summary>The latest version is known.</summary>
        None,

        /// <summary>Restore didn't resolve the package, so there is no version to look up from.</summary>
        Unresolved,

        /// <summary>The version range has no lower bound (such as <c>(, 2.0.0]</c>), so it isn't looked up.</summary>
        NoLowerBound,

        /// <summary>
        /// The dependency can be looked up, but no latest version was given. The built-in lookup always gives one (the
        /// resolved version when nothing newer is found, including when a source can't be reached), so only a model or
        /// <see cref="Services.INuGetPackageResolutionService"/> built elsewhere produces this.
        /// </summary>
        NotProvided
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
        /// The newest allowed version, or the resolved version when nothing newer is allowed; <see langword="null"/>
        /// when it couldn't be determined, and <see cref="LatestVersionUnknownReason"/> says why.
        /// </summary>
        public NuGetVersion? LatestVersion { get; init; }

        /// <summary>Why <see cref="LatestVersion"/> is unknown, or <see cref="Models.LatestVersionUnknownReason.None"/> when it is known.</summary>
        public LatestVersionUnknownReason LatestVersionUnknownReason => GetLatestVersionUnknownReason(Dependency, LatestVersion);

        /// <summary>How big the change from <see cref="Dependency.ResolvedVersion"/> to <see cref="LatestVersion"/> is.</summary>
        public DependencyUpgradeSeverity UpgradeSeverity => GetUpgradeSeverity(Dependency.ResolvedVersion, LatestVersion);

        /// <summary>Why <paramref name="latestVersion"/> of <paramref name="dependency"/> is unknown.</summary>
        public static LatestVersionUnknownReason GetLatestVersionUnknownReason(Dependency dependency, NuGetVersion? latestVersion)
        {
            System.ArgumentNullException.ThrowIfNull(dependency);

            if (latestVersion != null)
                return LatestVersionUnknownReason.None;
            if (dependency.ResolvedVersion == null)
                return LatestVersionUnknownReason.Unresolved;
            if (!CanLookUpLatestVersion(dependency))
                return LatestVersionUnknownReason.NoLowerBound;

            return LatestVersionUnknownReason.NotProvided;
        }

        /// <summary>
        /// A latest version is looked up from a resolved version and a range with a lower bound. Capping the range at a
        /// maximum version keeps its lower bound, so the declared range decides.
        /// </summary>
        internal static bool CanLookUpLatestVersion(Dependency dependency) =>
            dependency.ResolvedVersion != null && dependency.VersionRange.HasLowerBound;

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
