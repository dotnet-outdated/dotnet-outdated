using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;

namespace DotNetOutdated.Core.Services;

public interface IOutdatedDependencyService
{
    /// <summary>
    /// Finds the latest version of each dependency of <paramref name="projects"/> and reports those with a newer version
    /// (or every one, with <see cref="OutdatedOptions.IncludeUpToDate"/>).
    /// </summary>
    /// <returns>
    /// The projects with at least one reported dependency, ordered by name and then path. Target frameworks and
    /// projects with nothing to report are left out.
    /// </returns>
    Task<IReadOnlyList<OutdatedProject>> FindOutdatedAsync(IEnumerable<Project> projects, OutdatedOptions? options = null, CancellationToken cancellationToken = default);
}
