using NuGet.ProjectModel;

namespace DotNetOutdated.Services
{
    internal interface IDependencyGraphService
    {
        DependencyGraphSpec GenerateDependencyGraph(string projectPath);
    }
}