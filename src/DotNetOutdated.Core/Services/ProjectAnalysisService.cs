using DotNetOutdated.Core.Models;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;

namespace DotNetOutdated.Core.Services
{
    public class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;

        public ProjectAnalysisService(IDependencyGraphService dependencyGraphService, IDotNetRestoreService dotNetRestoreService, IFileSystem fileSystem)
        {
            _dependencyGraphService = dependencyGraphService;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
        }

        public List<Project> AnalyzeProject(string projectPath, bool runRestore, bool includeTransitiveDependencies, int transitiveDepth)
        {
            var dependencyGraph = _dependencyGraphService.GenerateDependencyGraph(projectPath);
            if (dependencyGraph == null)
                return null;

            var projects = new List<Project>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                // Restore the packages
                if (runRestore)
                {
                    _dotNetRestoreService.Restore(packageSpec.FilePath);
                }

                // Load the lock file
                string lockFilePath = _fileSystem.Path.Combine(packageSpec.RestoreMetadata.OutputPath, "project.assets.json");
                var lockFile = LockFileUtilities.GetLockFile(lockFilePath, NullLogger.Instance);

                // Create a project
                var project = new Project(packageSpec.Name, packageSpec.FilePath, packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList(), packageSpec.Version);
                projects.Add(project);

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
                                projectDependency.AutoReferenced, false, isDevelopmentDependency, projectDependency.VersionCentrallyManaged);
                            targetFramework.Dependencies.Add(dependency);

                            // Process transitive dependencies for the library
                            if (includeTransitiveDependencies)
                                AddDependencies(targetFramework, projectLibrary, target, 1, transitiveDepth);
                        }
                    }
                }
            }

            return projects;
        }

        private void AddDependencies(TargetFramework targetFramework, LockFileTargetLibrary parentLibrary, LockFileTarget target, int level, int transitiveDepth)
        {
            if (parentLibrary?.Dependencies != null)
            {
                foreach (var packageDependency in parentLibrary.Dependencies)
                {
                    var childLibrary = target.Libraries.FirstOrDefault(library => library.Name == packageDependency.Id);

                    // Only add library and process child dependencies if we have not come across this dependency before
                    if (!targetFramework.Dependencies.Any(dependency => dependency.Name == packageDependency.Id))
                    {
                        var childDependency = new Dependency(packageDependency.Id, packageDependency.VersionRange, childLibrary?.Version, false, true, false, false);
                        targetFramework.Dependencies.Add(childDependency);

                        // Process the dependency for this project dependency
                        if (level < transitiveDepth)
                            AddDependencies(targetFramework, childLibrary, target, level + 1, transitiveDepth);
                    }
                }
            }
        }
    }
}
