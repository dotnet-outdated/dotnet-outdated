using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Text.RegularExpressions;

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

            // When --no-restore is used, `dotnet add package` has an upstream bug where it writes
            // version info to .csproj instead of Directory.Packages.props for CPM projects.
            // See: https://github.com/NuGet/Home/issues/12552
            // Detect CPM and update Directory.Packages.props directly to avoid this.
            if (noRestore && TryUpdateCentralPackageVersion(projectPath, packageName, version))
            {
                return new RunStatus(string.Empty, string.Empty, 0);
            }

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

        private bool TryUpdateCentralPackageVersion(string projectPath, string packageName, NuGetVersion version)
        {
            var projectFile = _fileSystem.FileInfo.New(projectPath);
            var directory = projectFile.Directory;

            while (directory != null)
            {
                var files = directory.GetFiles("*", SearchOption.TopDirectoryOnly);
                IFileInfo cpvmFile = null;
                foreach (var file in files)
                {
                    if (file.Name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                    {
                        cpvmFile = file;
                        break;
                    }
                }

                if (cpvmFile != null)
                {
                    string fileContent;
                    using (var reader = cpvmFile.OpenText())
                    {
                        fileContent = reader.ReadToEnd();
                    }

                    if (fileContent.Contains($"\"{packageName}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        string newFileContent = Regex.Replace(
                            fileContent,
                            $"(<(?:PackageVersion|GlobalPackageReference)\\s*(?:Include|Update)=\"{Regex.Escape(packageName)}\"\\s*Version=\")([^\"]*)(\".*\\/>)",
                            m => $"{m.Groups[1].Captures[0].Value}{version}{m.Groups[3].Captures[0].Value}");

                        if (newFileContent != fileContent)
                        {
                            _fileSystem.File.WriteAllText(cpvmFile.FullName, newFileContent);
                        }

                        return true;
                    }
                }

                directory = directory.Parent;
            }

            return false;
        }
    }
}
