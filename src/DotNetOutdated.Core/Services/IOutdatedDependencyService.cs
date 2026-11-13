using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;

namespace DotNetOutdated.Core.Services;

public interface IOutdatedDependencyService
{
    /// <summary>
    /// Finds the projects at <paramref name="path"/>, analyzes them, and reports the latest version of each of their
    /// dependencies that has a newer one (or of every dependency, with <see cref="OutdatedOptions.IncludeUpToDate"/>).
    /// </summary>
    /// <param name="path">
    /// A project, solution, solution filter or file-based app, or a directory holding one (every project under it, with
    /// <see cref="OutdatedOptions.Recursive"/>).
    /// </param>
    /// <param name="progress">Told as each stage starts.</param>
    /// <returns>
    /// Every project analyzed, once even when it is reached by more than one path (such as a project that others
    /// reference, or one reached through a symbolic link), with every target framework.
    /// </returns>
    /// <exception cref="Exceptions.CommandValidationException">
    /// The path doesn't exist, holds no project or more than one, or a project couldn't be analyzed.
    /// </exception>
    Task<OutdatedReport> FindOutdatedAsync(
        string path,
        OutdatedOptions? options = null,
        IProgress<OutdatedReportStage>? progress = null,
        CancellationToken cancellationToken = default);

    // Kept only until the front ends call the path entry point; removed before release.
    Task<System.Collections.Generic.IReadOnlyList<OutdatedProject>> FindOutdatedAsync(System.Collections.Generic.IEnumerable<Project> projects, OutdatedOptions? options = null, CancellationToken cancellationToken = default);
}
