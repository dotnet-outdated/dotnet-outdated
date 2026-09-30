using DotNetOutdated.Core;
using DotNetOutdated.Core.Exceptions;
using NuGet.ProjectModel;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Analyzes the graph dependencies.
    /// </summary>
    /// <remarks>
    /// Credit for the stuff happening in here goes to the https://github.com/jaredcnance/dotnet-status project
    /// </remarks>
    public sealed class DependencyGraphService(IDotNetRunner dotNetRunner, IFileSystem fileSystem) : IDependencyGraphService
    {
        private readonly IDotNetRunner _dotNetRunner = dotNetRunner;
        private readonly IFileSystem _fileSystem = fileSystem;

        public async Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime)
        {
            var dgOutput = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetRandomFileName());
            var projectDirectory = _fileSystem.Path.GetDirectoryName(projectPath);
            var runStatus = projectPath.IsCSharpFile()
                ? GenerateFileBasedAppDependencyGraph(projectPath, projectDirectory, runtime, dgOutput)
                : GenerateProjectDependencyGraph(projectPath, projectDirectory, runtime, dgOutput);

            if (runStatus.IsSuccess)
            {
                var dependencyGraphText = await _fileSystem.File.ReadAllTextAsync(dgOutput).ConfigureAwait(false);
                return new ExtendedDependencyGraphSpec(dependencyGraphText);
            }

            var expectedProjectType = projectPath.IsCSharpFile()
                ? "a valid .NET file-based app? File-based app support requires .NET SDK 10.0.300 or later"
                : "a valid .NET Core or .NET Standard project type?";

            throw new CommandValidationException($"Unable to process the project `{projectPath}`. Are you sure this is {expectedProjectType}" +
                                                $"{Environment.NewLine}{Environment.NewLine}Here is the full error message returned from the Microsoft Build Engine:{Environment.NewLine}{Environment.NewLine}{runStatus.Output} - {runStatus.Errors} - exit code: {runStatus.ExitCode}");
        }

        public IReadOnlyList<PackageReferenceItem> GetPackageReferenceItems(string projectPath, string targetFramework)
        {
            string[] arguments =
            [
                "msbuild",
                projectPath,
                "-getItem:PackageReference",
                $"-p:TargetFramework={targetFramework}"
            ];

            var runStatus = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
            if (!runStatus.IsSuccess)
            {
                return [];
            }

            try
            {
                using var output = JsonDocument.Parse(runStatus.Output);
                if (!output.RootElement.TryGetProperty("Items", out var items) ||
                    !items.TryGetProperty("PackageReference", out var packageReferences))
                {
                    return [];
                }

                return packageReferences.EnumerateArray()
                    .Select(item => new PackageReferenceItem(
                        item.GetProperty("Identity").GetString(),
                        item.TryGetProperty("DefiningProjectFullPath", out var definingProject) ? definingProject.GetString() : null))
                    .ToList();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return [];
            }
        }

        private RunStatus GenerateProjectDependencyGraph(string projectPath, string projectDirectory, string runtime, string dgOutput)
        {
            List<string> arguments =
            [
                "msbuild",
                projectPath,
                "/p:NoWarn=NU1605",
                "/p:TreatWarningsAsErrors=false",
                "/t:Restore,GenerateRestoreGraphFile",
                $"/p:RestoreGraphOutputPath={dgOutput}"
            ];

            if (!string.IsNullOrEmpty(runtime))
            {
                arguments.Add($"/p:RuntimeIdentifiers={runtime}");
            }

            return _dotNetRunner.Run(projectDirectory, arguments.ToArray());
        }

        private RunStatus GenerateFileBasedAppDependencyGraph(string appPath, string projectDirectory, string runtime, string dgOutput)
        {
            List<string> restoreArguments =
            [
                "restore",
                appPath
            ];

            if (!string.IsNullOrEmpty(runtime))
            {
                restoreArguments.Add($"/p:RuntimeIdentifiers={runtime}");
            }

            var restoreStatus = _dotNetRunner.Run(projectDirectory, restoreArguments.ToArray());
            if (!restoreStatus.IsSuccess)
            {
                return restoreStatus;
            }

            List<string> buildArguments =
            [
                "build",
                appPath,
                "--no-restore",
                "/p:NoWarn=NU1605",
                "/p:TreatWarningsAsErrors=false",
                "/t:GenerateRestoreGraphFile",
                $"/p:RestoreGraphOutputPath={dgOutput}"
            ];

            if (!string.IsNullOrEmpty(runtime))
            {
                buildArguments.Add($"/p:RuntimeIdentifiers={runtime}");
            }

            return _dotNetRunner.Run(projectDirectory, buildArguments.ToArray());
        }
    }
}
