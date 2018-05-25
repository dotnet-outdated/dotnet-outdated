using System.IO.Abstractions;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using DotNetOutdated.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.ProjectModel;

namespace DotNetOutdated.Services
{
    /// <remarks>
    /// Credit for the stuff happening in here goes to the https://github.com/jaredcnance/dotnet-status project
    /// </remarks>
    internal class DependencyGraphService : IDependencyGraphService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DependencyGraphService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }
        
        public DependencyGraphSpec GenerateDependencyGraph(string projectPath)
        {
            string dgOutput = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetTempFileName());
            
            string[] arguments = new[] {"msbuild", projectPath, "/t:GenerateRestoreGraphFile", $"/p:RestoreGraphOutputPath={dgOutput}"};

            var runStatus = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);

            if (runStatus.IsSuccess)
            {
                string dependencyGraphText = _fileSystem.File.ReadAllText(dgOutput);
                return new DependencyGraphSpec(JsonConvert.DeserializeObject<JObject>(dependencyGraphText));
            }
            else
            {
                throw new CommandValidationException($"Unable to process the the project `{projectPath}. Are you sure this is a valid .NET Core or .NET Standard project type?" +
                                                     $"\r\n\r\nHere is the full error message returned from the Microsoft Build Engine:\r\n\r\n" + runStatus.Output);
            }
        }
    }
}