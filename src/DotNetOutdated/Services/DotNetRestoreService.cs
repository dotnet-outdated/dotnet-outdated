using System.IO.Abstractions;

namespace DotNetOutdated.Services
{
    internal class DotNetRestoreService : IDotNetRestoreService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DotNetRestoreService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }
        
        public RunStatus Restore(string projectPath)
        {
            string[] arguments = new[] {"restore", $"\"{projectPath}\""};

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }
    }
}