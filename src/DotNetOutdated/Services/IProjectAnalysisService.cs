using System.Collections.Generic;

namespace DotNetOutdated.Services
{
    internal interface IProjectAnalysisService
    {
        List<Project> AnalyzeProject(string projectPath);
    }
}