using DotNetOutdated.Core;
using DotNetOutdated.Core.Models;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace DotNetOutdated.Core.Services
{
    public class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;
        private readonly IVariableTrackingService _variableTrackingService;
        private readonly ILogger _logger;

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

                var usesCustomSdk = !isFileBasedApp && UsesCustomSdk(analyzedProjectPath);

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

                        if (usesCustomSdk)
                        {
                            MarkPackageDefinedReferencesAsAutoReferenced(analyzedProjectPath, targetFrameworkInformation.TargetAlias, packageSpec, targetFramework);
                        }
                    }
                }
            }

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
        /// Determines whether a project file declares an MSBuild SDK other than the <c>Microsoft.NET.Sdk</c> family.
        /// Such SDKs are usually resolved from NuGet and can add package references of their own.
        /// </summary>
        private bool UsesCustomSdk(string projectPath)
        {
            try
            {
                var root = XDocument.Parse(_fileSystem.File.ReadAllText(projectPath)).Root;
                if (root == null)
                {
                    return false;
                }

                var sdkNames = (root.Attribute("Sdk")?.Value ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Concat(root.Descendants().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value))
                    .Concat(root.Descendants().Where(e => e.Name.LocalName == "Import").Select(e => e.Attribute("Sdk")?.Value))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Split('/')[0].Trim());

                return sdkNames.Any(name => !name.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// Marks direct dependencies as auto-referenced when every <c>PackageReference</c> item for them is defined inside
        /// a NuGet package, such as the props or targets of an SDK like <c>Cake.Sdk</c> or <c>MSTest.Sdk</c>. Their versions
        /// come from the package, so <c>dotnet add package</c> cannot upgrade them in the project.
        /// </summary>
        private void MarkPackageDefinedReferencesAsAutoReferenced(string projectPath, string targetAlias, PackageSpec packageSpec, TargetFramework targetFramework)
        {
            var packageFolders = new[] { packageSpec.RestoreMetadata.PackagesPath }
                .Concat(packageSpec.RestoreMetadata.FallbackFolders ?? [])
                .Where(folder => !string.IsNullOrEmpty(folder))
                .Select(folder => _fileSystem.Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(folder)) + _fileSystem.Path.DirectorySeparatorChar)
                .ToList();

            var packageDefinedNames = (_dependencyGraphService.GetPackageReferenceItems(projectPath, targetAlias) ?? [])
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(items => items.All(item => IsInPackageFolder(item.DefiningProjectFullPath, packageFolders)))
                .Select(items => items.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, dependency) in targetFramework.Dependencies.ToList())
            {
                if (!dependency.IsTransitive && !dependency.IsAutoReferenced && packageDefinedNames.Contains(dependency.Name))
                {
                    targetFramework.Dependencies[key] = new Dependency(dependency.Name, dependency.VersionRange, dependency.ResolvedVersion,
                        isAutoReferenced: true, isTransitive: false, dependency.IsDevelopmentDependency);
                }
            }
        }

        private bool IsInPackageFolder(string path, List<string> packageFolders) =>
            !string.IsNullOrEmpty(path) &&
            packageFolders.Any(folder => _fileSystem.Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase));

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
