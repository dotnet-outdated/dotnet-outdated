using System.Collections.Generic;
using System.IO.Abstractions;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public class DotNetAddPackageService : IDotNetAddPackageService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DotNetAddPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, int timeout, bool ignoreFailedSource=false)
        {
            string projectName = _fileSystem.Path.GetFileName(projectPath);
            
            List<string> arguments = new List<string>{"add", $"\"{projectName}\"", "package", packageName, "-v", version.ToString(), "-f", $"\"{frameworkName}\"" };
            if (noRestore)
            {
                arguments.Add("--no-restore");
            }
            if (ignoreFailedSource)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments.ToArray(), timeout);
        }
    }
}