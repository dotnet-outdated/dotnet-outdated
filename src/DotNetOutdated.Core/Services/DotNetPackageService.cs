using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;

namespace DotNetOutdated.Core.Services
{
    public class DotNetPackageService : IDotNetPackageService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, TimeSpan timeout, bool ignoreFailedSources = false)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            string projectName = _fileSystem.Path.GetFileName(projectPath);

            List<string> arguments = new List<string> { "add", $"\"{projectName}\"", "package", packageName, "-v", version.ToString(), "-f", $"\"{frameworkName}\"" };
            if (noRestore)
            {
                arguments.Add("--no-restore");
            }
            if (ignoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments.ToArray(), timeout);
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
           var projectName = _fileSystem.Path.GetFileName(projectPath);
           var arguments = new[] { "remove", $"\"{projectName}\"", "package", packageName };

           return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }
    }
}
