using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace DotNetOutdated;

internal sealed class LocalToolManifestService(IFileSystem fileSystem, INuGetPackageResolutionService resolutionService)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly INuGetPackageResolutionService _resolutionService = resolutionService;

    public List<string> Discover(string path, Action<string> warn = null)
    {
        var fullPath = _fileSystem.Path.GetFullPath(path);
        if (!_fileSystem.Directory.Exists(fullPath) && !_fileSystem.File.Exists(fullPath))
            return [];

        var directory = _fileSystem.Directory.Exists(fullPath) ? fullPath : _fileSystem.Path.GetDirectoryName(fullPath);
        var manifests = new List<string>();
        while (directory != null)
        {
            foreach (var candidate in new[] {
                _fileSystem.Path.Combine(directory, ".config", "dotnet-tools.json"),
                _fileSystem.Path.Combine(directory, "dotnet-tools.json")
            })
            {
                if (!_fileSystem.File.Exists(candidate))
                    continue;

                Manifest manifest;
                try
                {
                    manifest = ReadManifest(candidate);
                }
                catch (CommandValidationException ex)
                {
                    // An unusable manifest must not block analysis of the project; it is not analyzed or modified.
                    warn?.Invoke($"Skipped local tool manifest {candidate}: {ex.Message}");
                    continue;
                }

                manifests.Add(candidate);
                if (manifest.IsRoot)
                    return manifests;
            }

            directory = _fileSystem.Directory.GetParent(directory)?.FullName;
        }
        return manifests;
    }

    public async Task<List<OutdatedProject>> FindOutdatedAsync(
        IReadOnlyList<string> manifests, string path, OutdatedOptions options)
    {
        if (manifests.Count == 0)
            return [];

        var projects = new List<OutdatedProject>();

        foreach (var manifestPath in manifests)
        {
            var manifestDirectory = _fileSystem.Path.GetDirectoryName(_fileSystem.Path.GetFullPath(manifestPath));
            var sources = SettingsUtility.GetEnabledSources(Settings.LoadDefaultSettings(manifestDirectory))
                .Select(source => source.SourceUri).ToList();
            var manifest = ReadManifest(manifestPath);
            var dependencies = new List<OutdatedDependency>();
            foreach (var tool in manifest.Tools)
            {
                if (options.IncludeFilters.Count != 0 && !options.IncludeFilters.Any(filter => tool.Name.Contains(filter, StringComparison.InvariantCultureIgnoreCase)) ||
                    options.ExcludeFilters.Any(filter => tool.Name.Contains(filter, StringComparison.InvariantCultureIgnoreCase)))
                    continue;

                var range = new VersionRange(tool.Version, true, options.MaximumVersion, true);
                var latest = await _resolutionService.ResolveLatestVersionAsync(new LatestVersionQuery
                {
                    PackageId = tool.Name,
                    CurrentVersion = tool.Version,
                    AllowedRange = range,
                    Sources = sources,
                    TargetFramework = NuGetFramework.AnyFramework,
                    ProjectFilePath = manifestPath,
                    IsDevelopmentDependency = true,
                    VersionLock = options.VersionLock,
                    Prerelease = options.Prerelease,
                    PrereleaseLabel = options.PrereleaseLabel,
                    OlderThanDays = options.OlderThanDays,
                    IgnoreFailedSources = options.IgnoreFailedSources,
                }).ConfigureAwait(false);

                if (latest != tool.Version || options.IncludeUpToDate)
                    dependencies.Add(new OutdatedDependency
                    {
                        Dependency = new Dependency { Name = tool.Name, VersionRange = range, ResolvedVersion = tool.Version },
                        LatestVersion = latest,
                    });
            }

            projects.Add(new OutdatedProject
            {
                Name = $"Local tools ({manifestPath})",
                FilePath = manifestPath,
                Sources = sources,
                TargetFrameworks = [new OutdatedTargetFramework
                {
                    Name = NuGetFramework.AnyFramework,
                    Dependencies = dependencies.OrderBy(dependency => dependency.Dependency.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                }],
            });
        }
        return projects;
    }

    public bool Upgrade(IReadOnlyList<OutdatedProject> projects, IConsole console)
    {
        foreach (var project in projects)
        {
            var manifestDirectory = _fileSystem.Path.GetDirectoryName(project.FilePath);
            var isConfigDirectory = string.Equals(_fileSystem.Path.GetFileName(manifestDirectory), ".config",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            foreach (var dependency in project.TargetFrameworks.SelectMany(framework => framework.Dependencies))
            {
                if (dependency.LatestVersion == null || dependency.LatestVersion <= dependency.Dependency.ResolvedVersion)
                    continue;

                if (!isConfigDirectory)
                    console.WriteLine("Use .NET SDK 10 or later for this command so NuGet loads the manifest directory's settings as well as user-wide settings. Check 'dotnet --version'; a global.json may select an older SDK.");

                console.WriteLine(
                    $"Skipped local tool {dependency.Dependency.Name}: --upgrade cannot refresh the manifest's command metadata. " +
                    $"Run 'dotnet tool update --local {dependency.Dependency.Name} --version {dependency.LatestVersion} --tool-manifest \"{project.FilePath}\"' " +
                    "to update it at the selected version and refresh its commands.");
            }
        }

        return true;
    }

    private Manifest ReadManifest(string path)
    {
        byte[] bytes;
        try
        {
            bytes = _fileSystem.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandValidationException($"Could not read local tool manifest '{path}': {ex.Message}", ex);
        }
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(offset));
            var root = document.RootElement;
            var isRoot = root.TryGetProperty("isRoot", out var rootFlag) && rootFlag.GetBoolean();
            var tools = new List<Tool>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("tools", out var toolsElement))
            {
                foreach (var tool in toolsElement.EnumerateObject())
                {
                    if (!NuGetVersion.TryParse(tool.Value.GetProperty("version").GetString(), out var version) ||
                        !names.Add(tool.Name))
                        throw new JsonException($"Invalid or duplicate tool version for '{tool.Name}'.");
                    tools.Add(new Tool(tool.Name, version));
                }
            }

            return new Manifest(isRoot, tools);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new CommandValidationException($"Invalid local tool manifest '{path}': {ex.Message}", ex);
        }
    }

    private sealed record Manifest(bool IsRoot, List<Tool> Tools);
    private sealed record Tool(string Name, NuGetVersion Version);
}
