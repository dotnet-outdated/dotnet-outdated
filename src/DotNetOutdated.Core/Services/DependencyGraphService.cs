using DotNetOutdated.Core.Exceptions;
using NuGet.ProjectModel;
using System;
using System.Collections.Generic;
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
    public sealed class DependencyGraphService(IDotNetRunner dotNetRunner, IFileSystem fileSystem) : IDependencyGraphService
    {
        public async Task<DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime)
        {
            var dgOutput = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), fileSystem.Path.GetTempFileName());
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

            var runStatus = dotNetRunner.Run(fileSystem.Path.GetDirectoryName(projectPath), arguments.ToArray());

            if (runStatus.IsSuccess)
            {
                var dependencyGraphText = await fileSystem.File.ReadAllTextAsync(dgOutput).ConfigureAwait(false);
                return new ExtendedDependencyGraphSpec(dependencyGraphText);
            }

            throw new CommandValidationException($"Unable to process the project `{projectPath}. Are you sure this is a valid .NET Core or .NET Standard project type?" +
                                                $"{Environment.NewLine}{Environment.NewLine}Here is the full error message returned from the Microsoft Build Engine:{Environment.NewLine}{Environment.NewLine}{runStatus.Output} - {runStatus.Errors} - exit code: {runStatus.ExitCode}");
        }
    }
}
