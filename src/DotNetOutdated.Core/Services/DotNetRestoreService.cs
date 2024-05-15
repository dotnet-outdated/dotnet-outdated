using System;
using System.IO.Abstractions;

namespace DotNetOutdated.Core.Services
{
    public class DotNetRestoreService : IDotNetRestoreService
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
            string[] arguments = new[] { "restore", $"\"{projectPath}\"" };

            var directoryName = _fileSystem.Path.GetDirectoryName(projectPath);

            ArgumentNullException.ThrowIfNull(directoryName);

            return _dotNetRunner.Run(directoryName, arguments);
        }
    }
}
