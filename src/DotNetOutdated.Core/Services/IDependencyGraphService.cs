using NuGet.ProjectModel;
using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        DependencyGraphSpec GenerateDependencyGraph(string projectPath, TimeSpan Timeout);
    }
}