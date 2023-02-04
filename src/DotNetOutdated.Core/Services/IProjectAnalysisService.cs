using DotNetOutdated.Core.Models;
using System.Collections.Generic;

namespace DotNetOutdated.Core.Services
{
    public interface IProjectAnalysisService
    {
        List<Project> AnalyzeProject(string projectPath, bool runRestore, bool includeTransitiveDependencies, int transitiveDepth);
    }
}
