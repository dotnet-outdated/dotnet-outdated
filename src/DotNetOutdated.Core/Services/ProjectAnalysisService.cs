using DotNetOutdated.Core;
using DotNetOutdated.Core.Models;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    internal class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IProjectEvaluator _evaluator;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;
        private readonly FileBasedAppDirectives _fileBasedAppDirectives;
        private readonly ILogger _logger;

        // Limits the concurrent `dotnet msbuild` evaluations for SDK-injected package references across all projects
        // being analyzed, since several projects can be analyzed at the same time.
        private readonly SemaphoreSlim _evaluationThrottle = new(Environment.ProcessorCount);

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem)
            : this(dependencyGraphService, dotNetRestoreService, fileSystem, NullLogger.Instance)
        {
        }

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem,
            ILogger logger)
            : this(new DotNetProjectEvaluator(dependencyGraphService, fileSystem), dotNetRestoreService, fileSystem, logger)
        {
        }

        internal ProjectAnalysisService(
            IProjectEvaluator evaluator,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem,
            ILogger logger)
        {
            _evaluator = evaluator;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
            _fileBasedAppDirectives = new FileBasedAppDirectives(fileSystem, evaluator);
            _logger = logger;
        }

        public async Task<IReadOnlyList<Project>> AnalyzeProjectAsync(string projectPath, ProjectAnalysisOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= ProjectAnalysisOptions.Default;
            var runtime = options.Runtime;

            // Generating the restore graph runs `dotnet msbuild` to completion, so don't start it for a cancelled request.
            cancellationToken.ThrowIfCancellationRequested();
            var dependencyGraph = await _evaluator.GetRestoreGraphAsync(projectPath, runtime).ConfigureAwait(false);

            var isFileBasedApp = projectPath.IsCSharpFile();
            var fileBasedAppPath = isFileBasedApp ? _fileSystem.Path.GetFullPath(projectPath) : null;
            var projects = new List<(Project Project, List<TargetFrameworkBuilder> TargetFrameworks)>();
            var sdkEvaluations = new List<SdkEvaluation>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                var analyzedProjectPath = fileBasedAppPath ?? packageSpec.FilePath!;

                // Restore the packages
                cancellationToken.ThrowIfCancellationRequested();

                if (options.RunRestore)
                {
                    _dotNetRestoreService.Restore(analyzedProjectPath);
                }

                // Load the lock file
                string lockFilePath = _fileSystem.Path.Combine(packageSpec.RestoreMetadata.OutputPath, "project.assets.json");
                var lockFile = LockFileUtilities.GetLockFile(lockFilePath, _logger);
                if (lockFile == null)
                    throw new InvalidOperationException($"Could not load lock file '{lockFilePath}' for project '{analyzedProjectPath}'. The file may be missing, unreadable, or in an unsupported format. Try running 'dotnet restore' on the project.");

                // Create a project
                var projectName = isFileBasedApp ? _fileSystem.Path.GetFileName(projectPath) : packageSpec.Name;
                var project = new Project
                {
                    Name = projectName,
                    FilePath = analyzedProjectPath,
                    Sources = packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList(),
                    Version = packageSpec.Version,
                };
                var targetFrameworks = new List<TargetFrameworkBuilder>();
                projects.Add((project, targetFrameworks));

                var projectSdkReferences = isFileBasedApp
                    ? []
                    : ProjectSdkReferenceHelper.Discover(_fileSystem, analyzedProjectPath);
                var packageFolders = !isFileBasedApp && SdkInjectedPackageReferences.UsesCustomSdk(_fileSystem, analyzedProjectPath)
                    ? GetPackageFolders(analyzedProjectPath, packageSpec)
                    : null;

                // Get the target frameworks with their dependencies
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new TargetFrameworkBuilder(targetFrameworkInformation.FrameworkName, targetFrameworkInformation.TargetAlias);
                    targetFrameworks.Add(targetFramework);

                    var target = lockFile.Targets.FirstOrDefault(t => t.TargetFramework.Equals(targetFrameworkInformation.FrameworkName));

                    if (target != null)
                    {
                        var directLibraries = new List<LockFileTargetLibrary?>();
                        foreach (var projectDependency in targetFrameworkInformation.Dependencies)
                        {
                            var projectLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, projectDependency.Name, StringComparison.OrdinalIgnoreCase));

                            bool isDevelopmentDependency = false;
                            if (projectLibrary != null)
                            {
                                // Determine whether this is a development dependency
                                var packageIdentity = new PackageIdentity(projectLibrary.Name!, projectLibrary.Version);
                                var packageInfo = LocalFolderUtility.GetPackageV3(packageSpec.RestoreMetadata.PackagesPath, packageIdentity, NullLogger.Instance);
                                if (packageInfo != null)
                                    isDevelopmentDependency = packageInfo.GetReader().GetDevelopmentDependency();
                            }

                            targetFramework.TryAdd(new Dependency
                            {
                                Name = projectDependency.Name,
                                VersionRange = projectDependency.LibraryRange.VersionRange!,
                                ResolvedVersion = projectLibrary?.Version,
                                IsAutoReferenced = projectDependency.AutoReferenced,
                                IsDevelopmentDependency = isDevelopmentDependency,
                            });
                            directLibraries.Add(projectLibrary);
                        }

                        // Walk the transitive dependencies only once every direct dependency is in, so a direct dependency
                        // that another one depends on isn't added as transitive first (TryAdd would then keep that entry).
                        if (options.IncludeTransitiveDependencies)
                        {
                            foreach (var projectLibrary in directLibraries)
                            {
                                AddDependencies(targetFramework, projectLibrary, target, 1, options.TransitiveDepth);
                            }
                        }

                        if (isFileBasedApp)
                        {
                            // Use the normalized full path so directive discovery (and its cache) keys off the
                            // same path used for restore and asset loading, even when the caller passed a relative path.
                            ApplyFileBasedAppDirectives(analyzedProjectPath, targetFramework);
                        }

                        foreach (var sdkReference in projectSdkReferences)
                        {
                            targetFramework.Set(new Dependency
                            {
                                Name = sdkReference.Name,
                                VersionRange = sdkReference.VersionRange,
                                ResolvedVersion = sdkReference.ResolvedVersion,
                                Kind = ReferenceKind.Sdk,
                            });
                        }

                        if (packageFolders != null)
                        {
                            sdkEvaluations.Add(new SdkEvaluation(analyzedProjectPath, targetFrameworkInformation.TargetAlias, runtime, packageFolders, targetFramework));
                        }
                    }
                }
            }

            // Each evaluation is a separate `dotnet msbuild` process, so run them side by side.
            await Task.WhenAll(sdkEvaluations.Select(evaluation => MarkPackageDefinedReferencesAsAutoReferencedAsync(evaluation, cancellationToken))).ConfigureAwait(false);

            return projects
                .Select(pair => pair.Project with { TargetFrameworks = pair.TargetFrameworks.Select(builder => builder.Build()).ToList() })
                .ToList();
        }

        private void ApplyFileBasedAppDirectives(string projectPath, TargetFrameworkBuilder targetFramework)
        {
            var fileBasedReferences = _fileBasedAppDirectives.References(projectPath);
            if (fileBasedReferences.Count == 0)
            {
                return;
            }

            // The directives are the authoritative source of direct dependencies for a file-based app: replace every
            // non-transitive graph dependency with the directive references.
            targetFramework.RemoveDirectDependencies();

            foreach (var reference in fileBasedReferences)
            {
                targetFramework.Set(new Dependency
                {
                    Name = reference.Name,
                    VersionRange = reference.VersionRange,
                    ResolvedVersion = reference.ResolvedVersion,
                    Kind = reference.Kind,
                });
            }
        }

        /// <summary>
        /// Marks direct dependencies as auto-referenced when every <c>PackageReference</c> item for them is defined inside
        /// a NuGet package, such as the props or targets of an SDK like <c>Cake.Sdk</c> or <c>MSTest.Sdk</c>.
        /// </summary>
        private async Task MarkPackageDefinedReferencesAsAutoReferencedAsync(SdkEvaluation evaluation, CancellationToken cancellationToken)
        {
            await _evaluationThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var items = await Task.Run(() => _evaluator.GetPackageReferenceItems(evaluation.ProjectPath, evaluation.TargetAlias, evaluation.Runtime), cancellationToken).ConfigureAwait(false);
                if (items == null)
                {
                    _logger.LogVerbose($"Could not evaluate the package references of '{evaluation.ProjectPath}' for {evaluation.TargetAlias}. " +
                        "Packages that its SDK adds are reported as direct dependencies.");
                    return;
                }

                var packageDefinedNames = SdkInjectedPackageReferences.GetPackageDefinedNames(_fileSystem, items, evaluation.PackageFolders);
                evaluation.TargetFramework.ReplaceAll(
                    SdkInjectedPackageReferences.MarkAsAutoReferenced(evaluation.TargetFramework.Dependencies, packageDefinedNames));
            }
            finally
            {
                _evaluationThrottle.Release();
            }
        }

        private List<string?> GetPackageFolders(string projectPath, PackageSpec packageSpec) =>
            // The SDK resolver finds SDKs in the global packages folder from the NuGet settings, while restore uses
            // RestorePackagesPath when it is set, so check both.
            new[] { packageSpec.RestoreMetadata.PackagesPath, GetConfiguredGlobalPackagesFolder(projectPath) }
                .Concat(packageSpec.RestoreMetadata.FallbackFolders ?? [])
                .ToList();

        private string? GetConfiguredGlobalPackagesFolder(string projectPath)
        {
            try
            {
                var settings = Settings.LoadDefaultSettings(_fileSystem.Path.GetDirectoryName(projectPath));
                return SettingsUtility.GetGlobalPackagesFolder(settings);
            }
            catch (Exception ex) when (ex is NuGetConfigurationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogVerbose($"Could not read the NuGet global packages folder for '{projectPath}': {ex.Message}");
                return null;
            }
        }

        private sealed record SdkEvaluation(string ProjectPath, string TargetAlias, string? Runtime, List<string?> PackageFolders, TargetFrameworkBuilder TargetFramework);

        /// <summary>
        /// The dependencies of a target framework while its project is analyzed, keyed by name and kind, since a project
        /// can reference a package and an SDK with the same id.
        /// </summary>
        private sealed class TargetFrameworkBuilder(NuGetFramework name, string? targetAlias)
        {
            private readonly Dictionary<(string Name, ReferenceKind Kind), Dependency> _dependencies = [];

            public IReadOnlyList<Dependency> Dependencies => [.. _dependencies.Values];

            public bool ContainsPackage(string name) => _dependencies.ContainsKey((name, ReferenceKind.Package));

            public void TryAdd(Dependency dependency) => _dependencies.TryAdd((dependency.Name, dependency.Kind), dependency);

            public void Set(Dependency dependency) => _dependencies[(dependency.Name, dependency.Kind)] = dependency;

            public void RemoveDirectDependencies()
            {
                foreach (var key in _dependencies.Where(pair => !pair.Value.IsTransitive).Select(pair => pair.Key).ToList())
                {
                    _dependencies.Remove(key);
                }
            }

            public void ReplaceAll(IEnumerable<Dependency> dependencies)
            {
                _dependencies.Clear();
                foreach (var dependency in dependencies)
                {
                    Set(dependency);
                }
            }

            public TargetFramework Build() => new() { Name = name, TargetAlias = targetAlias, Dependencies = Dependencies };
        }

        private void AddDependencies(TargetFrameworkBuilder targetFramework, LockFileTargetLibrary? parentLibrary, LockFileTarget target, int level, int transitiveDepth)
        {
            if (parentLibrary?.Dependencies != null)
            {
                foreach (var packageDependency in parentLibrary.Dependencies)
                {
                    var childLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, packageDependency.Id, StringComparison.OrdinalIgnoreCase));

                    // Only add library and process child dependencies if we have not come across this dependency before
                    if (!targetFramework.ContainsPackage(packageDependency.Id))
                    {
                        targetFramework.TryAdd(new Dependency
                        {
                            Name = packageDependency.Id,
                            VersionRange = packageDependency.VersionRange,
                            ResolvedVersion = childLibrary?.Version,
                            IsTransitive = true,
                        });

                        // Process the dependency for this project dependency
                        if (level < transitiveDepth)
                            AddDependencies(targetFramework, childLibrary, target, level + 1, transitiveDepth);
                    }
                }
            }
        }
    }
}
