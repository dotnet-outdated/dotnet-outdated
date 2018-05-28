using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    internal class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;

        public ProjectAnalysisService(IDependencyGraphService dependencyGraphService)
        {
            _dependencyGraphService = dependencyGraphService;
        }
        
        public List<Project> AnalyzeProject(string projectPath)
        {
            var dependencyGraph = _dependencyGraphService.GenerateDependencyGraph(projectPath);
            if (dependencyGraph == null)
                return null;

            var projects = new List<Project>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                // Create a project
                var project = new Project
                {
                    Name = packageSpec.Name,
                    Sources = packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList()
                };
                projects.Add(project);

                // Get the dependencies
                foreach (var libraryDependency in packageSpec.Dependencies)
                {
                    project.Dependencies.Add(new Project.Dependency
                    {
                        Name = libraryDependency.Name,
                        VersionRange = libraryDependency.LibraryRange.VersionRange
                    });
                }
                
                // Get the target frameworks with their dependencies 
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new Project.TargetFramework
                    {
                        Name = targetFrameworkInformation.FrameworkName.ToString(),
                    };
                    project.TargetFrameworks.Add(targetFramework);
                    
                    foreach (var libraryDependency in targetFrameworkInformation.Dependencies)
                    {
                        targetFramework.Dependencies.Add(new Project.Dependency
                        {
                            Name = libraryDependency.Name,
                            VersionRange = libraryDependency.LibraryRange.VersionRange
                        });
                    }
                }
            }

            return projects;
        }
    }
}