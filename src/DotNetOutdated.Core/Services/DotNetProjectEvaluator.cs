using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using NuGet.ProjectModel;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Evaluates projects without loading MSBuild into the process: the restore graph and <c>PackageReference</c> items
/// come from running <c>dotnet msbuild</c>, and version variables from a static reading of the project's XML and the
/// <c>Directory.*</c> files it imports (see <see cref="MSBuildEvaluator"/>).
/// </summary>
internal sealed class DotNetProjectEvaluator(IDependencyGraphService dependencyGraphService, IFileSystem fileSystem) : IProjectEvaluator
{
    private readonly IDependencyGraphService _dependencyGraphService = dependencyGraphService;
    private readonly MSBuildEvaluator _evaluator = new(fileSystem);

    public DotNetProjectEvaluator(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        : this(new DependencyGraphService(dotNetRunner, fileSystem), fileSystem)
    {
    }

    public Task<DependencyGraphSpec> GetRestoreGraphAsync(string projectPath, string? runtime) =>
        _dependencyGraphService.GenerateDependencyGraphAsync(projectPath, runtime);

    public IReadOnlyList<PackageReferenceItem>? GetPackageReferenceItems(string projectPath, string targetFramework, string? runtime) =>
        _dependencyGraphService.GetPackageReferenceItems(projectPath, targetFramework, runtime);

    public IReadOnlyDictionary<string, PackageVariableInfo> GetVersionVariables(string projectPath) =>
        _evaluator.PackageVariables(projectPath);

    public IReadOnlyDictionary<string, ImportedProperty> GetImportedProperties(string appPath) =>
        _evaluator.Evaluate(appPath, includeProjectFile: false).Properties
            .ToDictionary(
                pair => pair.Key,
                pair => new ImportedProperty(pair.Value.Name, pair.Value.RawValue, pair.Value.FilePath, pair.Value.ElementIndex, pair.Value.Ambiguous),
                StringComparer.OrdinalIgnoreCase);
}
