using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using NuGet.ProjectModel;

namespace DotNetOutdated.Services
{
    internal interface IDependencyGraphService
    {
        DependencyGraphSpec GenerateDependencyGraph(string projectPath);
    }
}