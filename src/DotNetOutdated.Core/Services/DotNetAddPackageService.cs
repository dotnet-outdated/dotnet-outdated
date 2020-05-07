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

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore)
        {
            string projectName = _fileSystem.Path.GetFileName(projectPath);
            
            List<string> arguments = new List<string>{"add", $"\"{projectName}\"", "package", packageName, "-v", version.ToString(), "-f", $"\"{frameworkName}\"" };
            if (noRestore)
            {
                arguments.Add("--no-restore");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments.ToArray());
        }
    }
}