using System.Collections.Generic;
using System.IO.Abstractions;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    using System.Linq;

    public class DotNetPackageService : IDotNetPackageService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
            return RunDotNet(projectPath, GetArguments(projectPath, "remove", packageName));
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSource=false)
        {
            List<string> args = new List<string> { "-v", version.ToString(), "-f", $"\"{frameworkName}\"" };
            if (noRestore)
            {
                args.Add("--no-restore");
            }
            if (ignoreFailedSource)
            {
                args.Add("--ignore-failed-sources");
            }

            return RunDotNet(projectPath, GetArguments(projectPath, "add", packageName, args));
        }

        private List<string> GetArguments(string projectPath, string command, string packageName, IEnumerable<string> args = null)
        {
            string projectName = _fileSystem.Path.GetFileName(projectPath);
            List<string> arguments = new List<string> { command, $"\"{projectName}\"", "package", packageName };
            arguments.AddRange(args ?? Enumerable.Empty<string>());
            return arguments;
        }

        private RunStatus RunDotNet(string projectPath, List<string> arguments)
        {
            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments.ToArray());
        }
    }
}