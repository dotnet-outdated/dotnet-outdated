using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal class ProjectAnalysisService : IProjectAnalysisService
    {
        private const int MaximumDependencyLevel = 1;
        
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;

        public ProjectAnalysisService(IDependencyGraphService dependencyGraphService, IDotNetRestoreService dotNetRestoreService, IFileSystem fileSystem)
        {
            _dependencyGraphService = dependencyGraphService;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
        }
        
        public List<Project> AnalyzeProject(string projectPath, bool includeTransitiveDependencies)
        {
            var dependencyGraph = _dependencyGraphService.GenerateDependencyGraph(projectPath);
            if (dependencyGraph == null)
                return null;

            var projects = new List<Project>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                // Restore the packages
                _dotNetRestoreService.Restore(packageSpec.FilePath);
                
                // Load the lock file
                string lockFilePath = _fileSystem.Path.Combine(packageSpec.RestoreMetadata.OutputPath, "project.assets.json");
                var lockFile = LockFileUtilities.GetLockFile(lockFilePath, NuGet.Common.NullLogger.Instance);
                
                // Create a project
                var project = new Project
                {
                    Name = packageSpec.Name,
                    Sources = packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList()
                };
                projects.Add(project);

                // Get the target frameworks with their dependencies 
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new Project.TargetFramework
                    {
                        Name = targetFrameworkInformation.FrameworkName,
                    };
                    project.TargetFrameworks.Add(targetFramework);

                    var target = lockFile.Targets.FirstOrDefault(t => t.TargetFramework.Equals(targetFrameworkInformation.FrameworkName));

                    foreach (var projectDependency in targetFrameworkInformation.Dependencies)
                    {
                        var projectLibrary = target.Libraries.FirstOrDefault(library => library.Name == projectDependency.Name);
                        
                        var dependency = new Project.Dependency
                        {
                            Name = projectDependency.Name,
                            VersionRange = projectDependency.LibraryRange.VersionRange,
                            ResolvedVersion = projectLibrary.Version
                        };
                        targetFramework.Dependencies.Add(dependency);
                        
                        // Process transitive dependencies for the library
                        if (includeTransitiveDependencies)
                            AddDependencies(dependency, projectLibrary, target, 1);
                    }
                }
            }

            return projects;
        }

        private void AddDependencies(Project.Dependency parentDependency, LockFileTargetLibrary parentLibrary, LockFileTarget target, int level)
        {
            foreach (var packageDependency in parentLibrary.Dependencies)
            {
                var childLibrary = target.Libraries.FirstOrDefault(library => library.Name == packageDependency.Id);
                
                var childDependency = new Project.Dependency
                {
                    Name = packageDependency.Id,
                    VersionRange = packageDependency.VersionRange,
                    ResolvedVersion = childLibrary.Version
                };
                parentDependency.Dependencies.Add(childDependency);

                // Process the dependency for this project depency
                if (level < MaximumDependencyLevel)
                    AddDependencies(childDependency, childLibrary, target, level + 1);
            }
        }
    }
}