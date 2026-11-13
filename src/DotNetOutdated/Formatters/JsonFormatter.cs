using DotNetOutdated.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class JsonFormatter : IOutputFormatter
{
    private static readonly JsonSerializerOptions jsonSerializerOptions = new() { WriteIndented = true };

    public async Task FormatAsync(IReadOnlyList<OutdatedProject> projects, TextWriter writer)
    {
        var report = new Report(projects
            .Select(project => new Project(
                project.Name,
                project.FilePath,
                project.TargetFrameworks
                    .Select(targetFramework => new TargetFramework(
                        targetFramework.Name.ToString(),
                        targetFramework.Dependencies
                            .Select(dependency => new Dependency(
                                dependency.Dependency.Name,
                                dependency.Dependency.ResolvedVersion?.ToString(),
                                dependency.LatestVersion?.ToString(),
                                dependency.UpgradeSeverity))
                            .ToList()))
                    .ToList()))
            .ToList());

        var json = JsonSerializer.Serialize(report, jsonSerializerOptions);
        await writer.WriteAsync(json).ConfigureAwait(false);
    }

    // The shape of the JSON report, which is a contract with whatever reads it.
    private sealed record Report(IReadOnlyList<Project> Projects);

    private sealed record Project(string Name, string FilePath, IReadOnlyList<TargetFramework> TargetFrameworks);

    private sealed record TargetFramework(string Name, IReadOnlyList<Dependency> Dependencies);

    private sealed record Dependency(
        string Name,
        string ResolvedVersion,
        string LatestVersion,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] DependencyUpgradeSeverity UpgradeSeverity);
}
