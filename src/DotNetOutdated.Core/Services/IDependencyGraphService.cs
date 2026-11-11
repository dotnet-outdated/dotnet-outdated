using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.ProjectModel;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string? runtime);

        /// <summary>
        /// Evaluates a project for one target framework and returns its <c>PackageReference</c> items,
        /// including the file that defined each item.
        /// </summary>
        /// <param name="projectPath">The project file.</param>
        /// <param name="targetFramework">The target framework alias to evaluate.</param>
        /// <param name="runtime">The runtime identifier passed to the restore graph, if any.</param>
        /// <returns>
        /// The evaluated items, or <c>null</c> when the project could not be evaluated. The default implementation
        /// returns an empty list.
        /// </returns>
        IReadOnlyList<PackageReferenceItem>? GetPackageReferenceItems(string projectPath, string targetFramework, string? runtime = null) => [];
    }

    /// <summary>
    /// An evaluated <c>PackageReference</c> item.
    /// </summary>
    /// <param name="Name">The package id.</param>
    /// <param name="DefiningProjectFullPath">The full path of the project, props or targets file that defined the item.</param>
    public sealed record PackageReferenceItem(string? Name, string? DefiningProjectFullPath);
}