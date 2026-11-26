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

    public List<string> Discover(string path)
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

                manifests.Add(candidate);
                if (ReadManifest(candidate).IsRoot)
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

        var fullPath = _fileSystem.Path.GetFullPath(path);
        var directory = _fileSystem.Directory.Exists(fullPath) ? fullPath : _fileSystem.Path.GetDirectoryName(fullPath);
        var sources = SettingsUtility.GetEnabledSources(Settings.LoadDefaultSettings(directory))
            .Select(source => source.SourceUri).ToList();
        var projects = new List<OutdatedProject>();

        foreach (var manifestPath in manifests)
        {
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
                    ProjectFilePath = _fileSystem.Path.Combine(directory, "dotnet-tools.json"),
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

    public bool Upgrade(IReadOnlyList<OutdatedProject> projects, UpgradeType upgradeType, IConsole console)
    {
        var upgradedAny = false;
        foreach (var project in projects)
        {
            foreach (var dependency in project.TargetFrameworks.SelectMany(framework => framework.Dependencies))
            {
                if (dependency.LatestVersion == null || dependency.LatestVersion <= dependency.Dependency.ResolvedVersion)
                    continue;

                if (upgradeType == UpgradeType.Prompt)
                {
                    var answer = Prompt.GetYesNo(console,
                        $"Upgrade local tool {dependency.Dependency.Name} in {project.FilePath} from {dependency.Dependency.ResolvedVersion} to {dependency.LatestVersion}?", true);
                    if (answer is null)
                    {
                        console.WriteLine("No input is available to answer the prompt, so the remaining tool upgrades were skipped.");
                        WriteRestoreHint(upgradedAny, console);
                        return true;
                    }
                    if (!answer.Value)
                        continue;
                }

                var manifest = ReadManifest(project.FilePath);
                var tool = manifest.Tools.SingleOrDefault(tool => string.Equals(tool.Name, dependency.Dependency.Name, StringComparison.OrdinalIgnoreCase));
                if (tool == null || tool.Version != dependency.Dependency.ResolvedVersion)
                    throw new CommandValidationException($"Local tool {dependency.Dependency.Name} in {project.FilePath} changed since it was analyzed.");

                var replacement = JsonSerializer.SerializeToUtf8Bytes(dependency.LatestVersion.ToString());
                var bytes = manifest.Bytes;
                byte[] updated = [.. bytes.AsSpan(0, tool.Start), .. replacement, .. bytes.AsSpan(tool.End)];
                try
                {
                    _fileSystem.File.WriteAllBytes(project.FilePath, updated);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var restoreNote = string.Empty;
                    try
                    {
                        // The write may have failed before changing anything, so only restore when the bytes differ.
                        if (!_fileSystem.File.ReadAllBytes(project.FilePath).AsSpan().SequenceEqual(bytes))
                            _fileSystem.File.WriteAllBytes(project.FilePath, bytes);
                    }
                    catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                    {
                        restoreNote = $" The original contents could not be restored ({restoreEx.Message}).";
                    }
                    throw new CommandValidationException($"Could not update local tool manifest '{project.FilePath}': {ex.Message}{restoreNote}", ex);
                }
                console.WriteLine($"Upgraded local tool {tool.Name} in {project.FilePath} to {dependency.LatestVersion}.");
                upgradedAny = true;
            }
        }
        WriteRestoreHint(upgradedAny, console);
        return true;
    }

    private static void WriteRestoreHint(bool upgradedAny, IConsole console)
    {
        if (upgradedAny)
            console.WriteLine("Run 'dotnet tool restore' to install the upgraded local tools.");
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
            var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("tools", out var toolsElement))
            {
                foreach (var tool in toolsElement.EnumerateObject())
                {
                    if (!NuGetVersion.TryParse(tool.Value.GetProperty("version").GetString(), out var version) ||
                        !versions.TryAdd(tool.Name, version))
                        throw new JsonException($"Invalid or duplicate tool version for '{tool.Name}'.");
                }
            }

            var tools = new List<Tool>();
            var reader = new Utf8JsonReader(bytes.AsSpan(offset));
            string currentTool = null;
            bool inTools = false;
            bool versionProperty = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    inTools = reader.ValueTextEquals("tools");
                else if (inTools && reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 2)
                    currentTool = reader.GetString();
                else if (inTools && reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 3)
                    versionProperty = reader.ValueTextEquals("version");
                else if (inTools && versionProperty && reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 3)
                {
                    tools.Add(new Tool(currentTool, versions[currentTool], offset + (int)reader.TokenStartIndex, offset + (int)reader.BytesConsumed));
                    versionProperty = false;
                }
            }
            if (tools.Count != versions.Count)
                throw new JsonException("Each tool must have exactly one version.");
            return new Manifest(bytes, isRoot, tools);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new CommandValidationException($"Invalid local tool manifest '{path}': {ex.Message}", ex);
        }
    }

    private sealed record Manifest(byte[] Bytes, bool IsRoot, List<Tool> Tools);
    private sealed record Tool(string Name, NuGetVersion Version, int Start, int End);
}
