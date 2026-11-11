using DotNetOutdated.Core.Services;
using NuGet.Frameworks;
using NuGet.Versioning;
using System;
using System.Collections.Generic;

namespace DotNetOutdated.Core.Models
{
    /// <summary>A project (or file-based app) and the dependencies of each of its target frameworks.</summary>
    public sealed record Project
    {
        public required string Name { get; init; }

        /// <summary>The full path of the project file or file-based app.</summary>
        public required string FilePath { get; init; }

        /// <summary>The package sources the project restores from.</summary>
        public IReadOnlyList<Uri> Sources { get => _sources; init => _sources = ReadOnlySnapshot.Of(value); }

        public required NuGetVersion Version { get; init; }

        public IReadOnlyList<TargetFramework> TargetFrameworks
        {
            get => _targetFrameworks;
            init => _targetFrameworks = ReadOnlySnapshot.Of(value);
        }

        private readonly IReadOnlyList<Uri> _sources = [];
        private readonly IReadOnlyList<TargetFramework> _targetFrameworks = [];
    }

    /// <summary>A target framework of a project and the dependencies it resolves.</summary>
    public sealed record TargetFramework
    {
        public required NuGetFramework Name { get; init; }

        /// <summary>The alias the project uses for the framework in <c>TargetFrameworks</c>, if any.</summary>
        public string? TargetAlias { get; init; }

        /// <summary>
        /// The references and transitive packages of the framework. A package and an SDK can have the same
        /// <see cref="Dependency.Name"/>; <see cref="Dependency.Kind"/> tells them apart.
        /// </summary>
        public IReadOnlyList<Dependency> Dependencies { get => _dependencies; init => _dependencies = ReadOnlySnapshot.Of(value); }

        private readonly IReadOnlyList<Dependency> _dependencies = [];
    }

    /// <summary>A package or SDK that a project resolves for one target framework.</summary>
    public sealed record Dependency
    {
        public required string Name { get; init; }

        /// <summary>The version range the project declares (or a transitive package's parent requires).</summary>
        public required VersionRange VersionRange { get; init; }

        /// <summary>The version restore resolved, or <see langword="null"/> when the package could not be resolved.</summary>
        public NuGetVersion? ResolvedVersion { get; init; }

        /// <summary>Whether the SDK, rather than the user, added the reference.</summary>
        public bool IsAutoReferenced { get; init; }

        /// <summary>Whether a reference brings the package in, rather than the project declaring it.</summary>
        public bool IsTransitive { get; init; }

        public bool IsDevelopmentDependency { get; init; }

        /// <summary>Whether this is a package or an SDK reference.</summary>
        public ReferenceKind Kind { get; init; } = ReferenceKind.Package;
    }
}
