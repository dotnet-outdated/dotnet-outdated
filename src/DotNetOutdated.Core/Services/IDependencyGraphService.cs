using System.Threading.Tasks;
using NuGet.ProjectModel;
using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime, TimeSpan timeout);
    }
}