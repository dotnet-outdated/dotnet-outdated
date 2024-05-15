﻿using NuGet.Versioning;
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

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources = false)
        {
            if (version == null)
                throw new ArgumentNullException(nameof(version));

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

           var directoryName = _fileSystem.Path.GetDirectoryName(projectPath);

           ArgumentNullException.ThrowIfNull(directoryName);

            return _dotNetRunner.Run(directoryName, arguments.ToArray());
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
           var projectName = _fileSystem.Path.GetFileName(projectPath);
           var arguments = new[] { "remove", $"\"{projectName}\"", "package", packageName };

           var directoryName = _fileSystem.Path.GetDirectoryName(projectPath);

           ArgumentNullException.ThrowIfNull(directoryName);

           return _dotNetRunner.Run(directoryName, arguments);
        }
    }
}
