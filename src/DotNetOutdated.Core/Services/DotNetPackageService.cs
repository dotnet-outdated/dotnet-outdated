using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;

namespace DotNetOutdated.Core.Services
{
    public class DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem) : IDotNetPackageService
    {
        private readonly IDotNetRunner _dotNetRunner = dotNetRunner;
        private readonly IFileSystem _fileSystem = fileSystem;

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources = false)
        {
            ArgumentNullException.ThrowIfNull(version);

            string projectName = _fileSystem.Path.GetFileName(projectPath);

            List<string> arguments = ["add", projectName, "package", packageName, "-v", version.ToString(), "-f", frameworkName];
            if (noRestore)
            {
                arguments.Add("--no-restore");
            }
            if (ignoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), [.. arguments]);
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
           var projectName = _fileSystem.Path.GetFileName(projectPath);
           string[] arguments = ["remove", projectName, "package", packageName];

           return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }
    }
}
