using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.ProjectModel;

namespace DotNetOutdated.Core.Services
{
    public interface IDependencyGraphService
    {
        Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime);

        /// <summary>
        /// Evaluates a project for one target framework and returns its <c>PackageReference</c> items,
        /// including the file that defined each item.
        /// </summary>
        /// <returns>The evaluated items, or an empty list when the project could not be evaluated.</returns>
        IReadOnlyList<PackageReferenceItem> GetPackageReferenceItems(string projectPath, string targetFramework) => [];
    }

    /// <summary>
    /// An evaluated <c>PackageReference</c> item.
    /// </summary>
    /// <param name="Name">The package id.</param>
    /// <param name="DefiningProjectFullPath">The full path of the project, props or targets file that defined the item.</param>
    public sealed record PackageReferenceItem(string Name, string DefiningProjectFullPath);
}