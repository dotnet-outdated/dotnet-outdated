using DotNetOutdated.Core;
using DotNetOutdated.Core.Models;
using NuGet.Common;
using NuGet.Configuration;
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
    public class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;
        private readonly IVariableTrackingService _variableTrackingService;
        private readonly ILogger _logger;

        // Limits the concurrent `dotnet msbuild` evaluations for SDK-injected package references across all projects
        // being analyzed, since several projects can be analyzed at the same time.
        private readonly SemaphoreSlim _evaluationThrottle = new(Environment.ProcessorCount);

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem)
            : this(dependencyGraphService, dotNetRestoreService, fileSystem, new VariableTrackingService(fileSystem))
        {
        }

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem,
            ILogger logger)
            : this(dependencyGraphService, dotNetRestoreService, fileSystem, new VariableTrackingService(fileSystem), logger)
        {
        }

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem,
            IVariableTrackingService variableTrackingService)
            : this(dependencyGraphService, dotNetRestoreService, fileSystem, variableTrackingService, NullLogger.Instance)
        {
        }

        public ProjectAnalysisService(
            IDependencyGraphService dependencyGraphService,
            IDotNetRestoreService dotNetRestoreService,
            IFileSystem fileSystem,
            IVariableTrackingService variableTrackingService,
            ILogger logger)
        {
            _dependencyGraphService = dependencyGraphService;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
            _variableTrackingService = variableTrackingService;
            _logger = logger;
        }

        public async Task<List<Project>> AnalyzeProjectAsync(string projectPath, bool runRestore, bool includeTransitiveDependencies, int transitiveDepth,  string runtime)
        {
            var dependencyGraph = await _dependencyGraphService.GenerateDependencyGraphAsync(projectPath, runtime).ConfigureAwait(false);
            if (dependencyGraph == null)
                return null;

            var isFileBasedApp = projectPath.IsCSharpFile();
            var projectFilePath = isFileBasedApp ? _fileSystem.Path.GetFullPath(projectPath) : null;
            var projects = new List<Project>();
            var sdkEvaluations = new List<SdkEvaluation>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                var analyzedProjectPath = isFileBasedApp ? projectFilePath : packageSpec.FilePath;

                // Restore the packages
                if (runRestore)
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
                var project = new Project(projectName, analyzedProjectPath, packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList(), packageSpec.Version);
                projects.Add(project);

                var packageFolders = !isFileBasedApp && SdkInjectedPackageReferences.UsesCustomSdk(_fileSystem, analyzedProjectPath)
                    ? GetPackageFolders(analyzedProjectPath, packageSpec)
                    : null;

                // Get the target frameworks with their dependencies
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new TargetFramework(targetFrameworkInformation.FrameworkName);
                    project.TargetFrameworks.Add(targetFramework);

                    var target = lockFile.Targets.FirstOrDefault(t => t.TargetFramework.Equals(targetFrameworkInformation.FrameworkName));

                    if (target != null)
                    {
                        foreach (var projectDependency in targetFrameworkInformation.Dependencies)
                        {
                            var projectLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, projectDependency.Name, StringComparison.OrdinalIgnoreCase));

                            bool isDevelopmentDependency = false;
                            if (projectLibrary != null)
                            {
                                // Determine whether this is a development dependency
                                var packageIdentity = new PackageIdentity(projectLibrary.Name, projectLibrary.Version);
                                var packageInfo = LocalFolderUtility.GetPackageV3(packageSpec.RestoreMetadata.PackagesPath, packageIdentity, NullLogger.Instance);
                                if (packageInfo != null)
                                    isDevelopmentDependency = packageInfo.GetReader().GetDevelopmentDependency();
                            }

                            var dependency = new Dependency(projectDependency.Name, projectDependency.LibraryRange.VersionRange, projectLibrary?.Version,
                                projectDependency.AutoReferenced, false, isDevelopmentDependency);
                            targetFramework.Dependencies.TryAdd(dependency.Name, dependency);

                            // Process transitive dependencies for the library
                            if (includeTransitiveDependencies)
                                AddDependencies(targetFramework, projectLibrary, target, 1, transitiveDepth);
                        }

                        if (isFileBasedApp)
                        {
                            // Use the normalized full path so directive discovery (and its cache) keys off the
                            // same path used for restore and asset loading, even when the caller passed a relative path.
                            ApplyFileBasedAppDirectives(analyzedProjectPath, targetFramework);
                        }

                        if (packageFolders != null)
                        {
                            sdkEvaluations.Add(new SdkEvaluation(analyzedProjectPath, targetFrameworkInformation.TargetAlias, runtime, packageFolders, targetFramework));
                        }
                    }
                }
            }

            // Each evaluation is a separate `dotnet msbuild` process, so run them side by side.
            await Task.WhenAll(sdkEvaluations.Select(MarkPackageDefinedReferencesAsAutoReferencedAsync)).ConfigureAwait(false);

            return projects;
        }

        private void ApplyFileBasedAppDirectives(string projectPath, TargetFramework targetFramework)
        {
            var fileBasedReferences = _variableTrackingService.DiscoverFileBasedAppReferences(projectPath);
            if (fileBasedReferences.Count == 0)
            {
                return;
            }

            // The directives are the authoritative source of direct dependencies for a file-based app.
            // Remove every non-transitive graph dependency (including graph entries for packages that are
            // also declared as directives) and re-add the directive references below. Re-adding under the
            // directive keys would otherwise leave a duplicate graph entry keyed by the plain package name.
            var directDependencyKeys = targetFramework.Dependencies
                .Where(pair => !pair.Value.IsTransitive)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var dependencyKey in directDependencyKeys)
            {
                targetFramework.Dependencies.Remove(dependencyKey);
            }

            foreach (var reference in fileBasedReferences)
            {
                targetFramework.Dependencies[FileBasedAppReferenceHelper.GetDependencyDictionaryKey(reference)] = new Dependency(
                    reference.Name,
                    reference.VersionRange,
                    reference.ResolvedVersion,
                    isAutoReferenced: false,
                    isTransitive: false,
                    isDevelopmentDependency: false);
            }
        }

        /// <summary>
        /// Marks direct dependencies as auto-referenced when every <c>PackageReference</c> item for them is defined inside
        /// a NuGet package, such as the props or targets of an SDK like <c>Cake.Sdk</c> or <c>MSTest.Sdk</c>.
        /// </summary>
        private async Task MarkPackageDefinedReferencesAsAutoReferencedAsync(SdkEvaluation evaluation)
        {
            await _evaluationThrottle.WaitAsync().ConfigureAwait(false);
            try
            {
                var items = await Task.Run(() => _dependencyGraphService.GetPackageReferenceItems(evaluation.ProjectPath, evaluation.TargetAlias, evaluation.Runtime)).ConfigureAwait(false);
                if (items == null)
                {
                    _logger.LogVerbose($"Could not evaluate the package references of '{evaluation.ProjectPath}' for {evaluation.TargetAlias}. " +
                        "Packages that its SDK adds are reported as direct dependencies.");
                    return;
                }

                var packageDefinedNames = SdkInjectedPackageReferences.GetPackageDefinedNames(_fileSystem, items, evaluation.PackageFolders);
                SdkInjectedPackageReferences.MarkAsAutoReferenced(evaluation.TargetFramework, packageDefinedNames);
            }
            finally
            {
                _evaluationThrottle.Release();
            }
        }

        private List<string> GetPackageFolders(string projectPath, PackageSpec packageSpec) =>
            // The SDK resolver finds SDKs in the global packages folder from the NuGet settings, while restore uses
            // RestorePackagesPath when it is set, so check both.
            new[] { packageSpec.RestoreMetadata.PackagesPath, GetConfiguredGlobalPackagesFolder(projectPath) }
                .Concat(packageSpec.RestoreMetadata.FallbackFolders ?? [])
                .ToList();

        private string GetConfiguredGlobalPackagesFolder(string projectPath)
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

        private sealed record SdkEvaluation(string ProjectPath, string TargetAlias, string Runtime, List<string> PackageFolders, TargetFramework TargetFramework);

        private void AddDependencies(TargetFramework targetFramework, LockFileTargetLibrary parentLibrary, LockFileTarget target, int level, int transitiveDepth)
        {
            if (parentLibrary?.Dependencies != null)
            {
                foreach (var packageDependency in parentLibrary.Dependencies)
                {
                    var childLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, packageDependency.Id, StringComparison.OrdinalIgnoreCase));

                    // Only add library and process child dependencies if we have not come across this dependency before
                    if (!targetFramework.Dependencies.ContainsKey(packageDependency.Id))
                    {
                        var childDependency = new Dependency(packageDependency.Id, packageDependency.VersionRange, childLibrary?.Version, false, true, false);
                        targetFramework.Dependencies.Add(childDependency.Name, childDependency);

                        // Process the dependency for this project dependency
                        if (level < transitiveDepth)
                            AddDependencies(targetFramework, childLibrary, target, level + 1, transitiveDepth);
                    }
                }
            }
        }
    }
}
