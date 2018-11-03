using System.Collections.Generic;
using DotNetOutdated.Models;

namespace DotNetOutdated.Services
{
    internal interface IProjectAnalysisService
    {
        List<Project> AnalyzeProject(string projectPath, bool includeTransitiveDependencies, int transitiveDepth);
    }
}