using System.IO.Abstractions;

namespace DotNetOutdated.Core.Services
{
    public class DotNetRestoreService(IDotNetRunner dotNetRunner, IFileSystem fileSystem) : IDotNetRestoreService
    {
        private readonly IDotNetRunner _dotNetRunner = dotNetRunner;
        private readonly IFileSystem _fileSystem = fileSystem;

        public RunStatus Restore(string projectPath)
        {
            string[] arguments = ["restore", projectPath];

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }
    }
}
