using System.Threading.Tasks;
using NuGet.ProjectModel;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime, int commandTimeOut = 20000);
    }
}