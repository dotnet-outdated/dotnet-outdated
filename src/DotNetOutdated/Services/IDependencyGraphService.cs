using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace DotNetOutdated.Services
{
    public interface IDependencyGraphService
    {
        void GenerateDependencyGraph(string projectPath);
    }
}