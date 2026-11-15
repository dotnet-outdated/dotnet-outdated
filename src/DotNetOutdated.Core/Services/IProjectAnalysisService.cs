using DotNetOutdated.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface IProjectAnalysisService
    {
        /// <summary>
        /// Reads the references and resolved dependencies of a project, solution, solution filter or file-based app.
        /// A solution yields one <see cref="Project"/> per restorable project.
        /// </summary>
        Task<IReadOnlyList<Project>> AnalyzeProjectAsync(string projectPath, ProjectAnalysisOptions? options = null, CancellationToken cancellationToken = default);
    }
}
