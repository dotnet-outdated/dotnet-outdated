using DotNetOutdated.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface IProjectAnalysisService
    {
        Task<List<Project>> AnalyzeProjectAsync(string projectPath, bool runRestore, bool includeTransitiveDependencies, int transitiveDepth, string runtime, TimeSpan timeout);
    }
}
