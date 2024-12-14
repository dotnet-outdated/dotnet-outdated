using DotNetOutdated.Core.Exceptions;
using NuGet.ProjectModel;
using System;
using System.IO.Abstractions;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Analyzes the graph dependencies.
    /// </summary>
    /// <remarks>
    /// Credit for the stuff happening in here goes to the https://github.com/jaredcnance/dotnet-status project
    /// </remarks>
    public sealed class DependencyGraphService : IDependencyGraphService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DependencyGraphService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }

        public async Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime)
        {
            var dgOutput = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetTempFileName());
            string[] arguments =
            [
                "msbuild",
                $"\"{projectPath}\"",
                "/p:NoWarn=NU1605",
                "/p:TreatWarningsAsErrors=false",
                "/t:Restore,GenerateRestoreGraphFile",
                $"/p:RestoreGraphOutputPath=\"{dgOutput}\"",
                $"/p:RuntimeIdentifiers=\"{runtime}\""

            ];

            var runStatus = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);

            if (runStatus.IsSuccess)
            {
                var dependencyGraphText = await _fileSystem.File.ReadAllTextAsync(dgOutput).ConfigureAwait(false);
                return new ExtendedDependencyGraphSpec(dependencyGraphText);
            }

            throw new CommandValidationException($"Unable to process the project `{projectPath}. Are you sure this is a valid .NET Core or .NET Standard project type?" +
                                                $"{Environment.NewLine}{Environment.NewLine}Here is the full error message returned from the Microsoft Build Engine:{Environment.NewLine}{Environment.NewLine}{runStatus.Output} - {runStatus.Errors} - exit code: {runStatus.ExitCode}");
        }
    }
}
