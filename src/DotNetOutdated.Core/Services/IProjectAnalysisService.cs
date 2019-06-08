using System.Collections.Generic;
using DotNetOutdated.Core.Models;

namespace DotNetOutdated.Core.Services
{
    public interface IProjectAnalysisService
    {
        List<Project> AnalyzeProject(string projectPath, bool includeTransitiveDependencies, int transitiveDepth);
    }
}