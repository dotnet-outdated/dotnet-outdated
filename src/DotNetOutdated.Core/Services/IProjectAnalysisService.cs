using DotNetOutdated.Core.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface IProjectAnalysisService
    {
        Task<List<Project>> AnalyzeProjectAsync(string projectPath, bool runRestore, bool includeTransitiveDependencies, int transitiveDepth);
    }
}
