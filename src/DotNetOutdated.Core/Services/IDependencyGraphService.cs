using NuGet.ProjectModel;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        DependencyGraphSpec GenerateDependencyGraph(string projectPath);
    }
}