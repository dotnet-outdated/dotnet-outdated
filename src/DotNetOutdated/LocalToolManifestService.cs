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

    public bool Upgrade(IReadOnlyList<OutdatedProject> projects, UpgradeType upgradeType, IConsole console, string path)
    {
        var upgradedAny = false;
        var scope = GetScope(path);
        var committed = new List<string>();
        try
        {
            var result = UpgradeTools(projects, upgradeType, console, scope, committed, ref upgradedAny);
            WriteRestoreHint(upgradedAny, console);
            return result;
        }
        catch when (upgradedAny)
        {
            // Earlier upgrades still need a restore even when a later one fails.
            // A failure writing the hint must not mask the original exception.
            try
            {
                console.WriteLine($"The upgrade stopped early. Already written: {string.Join("; ", committed)}.");
                WriteRestoreHint(upgradedAny, console);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    private string GetScope(string path)
    {
        var fullPath = _fileSystem.Path.GetFullPath(path);
        return _fileSystem.Directory.Exists(fullPath) ? fullPath : _fileSystem.Path.GetDirectoryName(fullPath);
    }

    private bool IsWritable(string manifestPath, string scope)
    {
        var relative = _fileSystem.Path.GetRelativePath(scope, _fileSystem.Path.GetFullPath(manifestPath));
        if (relative == ".." || relative.StartsWith(".." + _fileSystem.Path.DirectorySeparatorChar, StringComparison.Ordinal) || _fileSystem.Path.IsPathRooted(relative))
            return false;

        // A link on any directory between the manifest and the scope could redirect the write outside the scope.
        var fullScope = _fileSystem.Path.GetFullPath(scope).TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar);
        var directory = _fileSystem.Path.GetDirectoryName(_fileSystem.Path.GetFullPath(manifestPath));
        while (!string.IsNullOrEmpty(directory)
            && !string.Equals(directory.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar), fullScope, StringComparison.Ordinal))
        {
            if (_fileSystem.DirectoryInfo.New(directory).LinkTarget != null)
                return false;
            directory = _fileSystem.Path.GetDirectoryName(directory);
        }

        return _fileSystem.FileInfo.New(manifestPath).LinkTarget == null;
    }

    private bool UpgradeTools(IReadOnlyList<OutdatedProject> projects, UpgradeType upgradeType, IConsole console, string scope, List<string> committed, ref bool upgradedAny)
    {
        foreach (var project in projects)
        {
            if (!IsWritable(project.FilePath, scope))
            {
                console.WriteLine($"Skipped local tool manifest {project.FilePath}: it is outside the analyzed directory or is a symbolic link, so it was not modified.");
                continue;
            }

            var approved = new List<OutdatedDependency>();
            var stopped = false;
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
                        stopped = true;
                        break;
                    }
                    if (!answer.Value)
                        continue;
                }

                approved.Add(dependency);
            }

            if (approved.Count != 0)
            {
                // All approved changes to one manifest are committed with a single replacement, so a manifest is never left half-upgraded.
                if (!IsWritable(project.FilePath, scope))
                    throw new CommandValidationException($"Local tool manifest '{project.FilePath}' changed location or became a link while it was being upgraded; it was left unchanged.");

                var manifest = ReadManifest(project.FilePath);
                var changes = new List<(Tool Tool, OutdatedDependency Dependency)>();
                foreach (var dependency in approved)
                {
                    var tool = manifest.Tools.SingleOrDefault(tool => string.Equals(tool.Name, dependency.Dependency.Name, StringComparison.OrdinalIgnoreCase));
                    if (tool == null || tool.Version != dependency.Dependency.ResolvedVersion)
                        throw new CommandValidationException($"Local tool {dependency.Dependency.Name} in {project.FilePath} changed since it was analyzed.");
                    changes.Add((tool, dependency));
                }

                var bytes = manifest.Bytes;
                var updated = bytes;
                foreach (var (tool, dependency) in changes.OrderByDescending(change => change.Tool.Start))
                {
                    var replacement = JsonSerializer.SerializeToUtf8Bytes(dependency.LatestVersion.ToString());
                    updated = [.. updated.AsSpan(0, tool.Start), .. replacement, .. updated.AsSpan(tool.End)];
                }

                var temporaryPath = project.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    _fileSystem.File.WriteAllBytes(temporaryPath, updated);
                    // Narrow the lost-update window: refuse to replace a manifest edited since it was read.
                    if (!_fileSystem.File.ReadAllBytes(project.FilePath).AsSpan().SequenceEqual(bytes))
                        throw new CommandValidationException($"Local tool manifest '{project.FilePath}' was modified while it was being upgraded; it was left unchanged.");
                    _fileSystem.File.Move(temporaryPath, project.FilePath, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CommandValidationException($"Could not update local tool manifest '{project.FilePath}': {ex.Message}. The manifest was left unchanged.", ex);
                }
                finally
                {
                    try
                    {
                        if (_fileSystem.File.Exists(temporaryPath))
                            _fileSystem.File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                foreach (var (tool, dependency) in changes)
                {
                    console.WriteLine($"Upgraded local tool {tool.Name} in {project.FilePath} to {dependency.LatestVersion}.");
                    committed.Add($"{tool.Name} in {project.FilePath}");
                }
                upgradedAny = true;
            }

            if (stopped)
                return true;
        }
        return true;
    }

    private static void WriteRestoreHint(bool upgradedAny, IConsole console)
    {
        if (upgradedAny)
            console.WriteLine("Run 'dotnet tool restore' from the analyzed directory (or pass '--tool-manifest <path>') to install the upgraded local tools. Tool commands in the manifest are not changed; use 'dotnet tool update --local' if a new version renames its commands.");
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
