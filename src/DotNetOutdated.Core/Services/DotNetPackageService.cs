using DotNetOutdated.Core;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services
{
    public class DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem, IVariableTrackingService variableTrackingService) : IDotNetPackageService
    {
        private readonly IDotNetRunner _dotNetRunner = dotNetRunner;
        private readonly IFileSystem _fileSystem = fileSystem;
        private readonly IVariableTrackingService _variableTrackingService = variableTrackingService;

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources = false)
        {
            ArgumentNullException.ThrowIfNull(version);

            // Check if this package uses a variable reference
            var variables = _variableTrackingService.DiscoverPackageVariables(projectPath);
            variables.TryGetValue(packageName, out PackageVariableInfo variableInfo);

            if (variableInfo?.ElementType == PackageVariableInfo.FileBasedPackageDirectiveElementType ||
                variableInfo?.ElementType == PackageVariableInfo.FileBasedSdkDirectiveElementType)
            {
                if (!_variableTrackingService.TryUpdatePackageVariable(variableInfo, version) &&
                    !IsFileBasedAppVariableAtRequestedVersion(variableInfo, version))
                {
                    return FileBasedAppUpdateFailed(
                        projectPath,
                        packageName,
                        "Could not update the matching #:property value or #:package/#:sdk directive.");
                }

                return noRestore
                    ? new RunStatus(string.Empty, string.Empty, 0)
                    : RestoreProject(projectPath, ignoreFailedSources);
            }

            if (projectPath.IsCSharpFile())
            {
                var fileBasedReference = _variableTrackingService.DiscoverFileBasedAppReferences(projectPath)
                    .FirstOrDefault(reference =>
                        string.Equals(reference.Name, packageName, StringComparison.OrdinalIgnoreCase) &&
                        reference.VariableInfo == null);

                if (fileBasedReference?.Kind == FileBasedAppReferenceKind.Sdk)
                {
                    if (fileBasedReference.UsesPropertyReferences)
                    {
                        return FileBasedAppUpdateFailed(
                            projectPath,
                            packageName,
                            "Direct #:sdk updates require a literal id@version (for example #:sdk Cake.Sdk@6.0.0); " +
                            "property references in the name or version must be changed via #:property lines.");
                    }

                    if (!_variableTrackingService.UpdateFileBasedAppDirectReference(projectPath, packageName, fileBasedReference.Kind, version) &&
                        !IsFileBasedAppDirectReferenceAtRequestedVersion(fileBasedReference, version))
                    {
                        return FileBasedAppUpdateFailed(
                            projectPath,
                            packageName,
                            $"Expected a literal #:sdk {packageName}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
                    }

                    return noRestore
                        ? new RunStatus(string.Empty, string.Empty, 0)
                        : RestoreProject(projectPath, ignoreFailedSources);
                }
            }

            // When --no-restore is used, `dotnet add package` has an upstream bug where it writes
            // version info to .csproj instead of Directory.Packages.props for CPM projects.
            // See: https://github.com/NuGet/Home/issues/12552
            if (noRestore)
            {
                // For CPM projects with a variable reference, update the variable directly.
                // This avoids the dotnet CPM bug AND preserves the variable reference.
                if (variableInfo != null && variableInfo.ElementType != "PackageReference")
                {
                    _variableTrackingService.UpdatePackageVariable(variableInfo, version);
                    return new RunStatus(string.Empty, string.Empty, 0);
                }

                // For CPM projects without a variable reference, update Directory.Packages.props directly.
                if (TryUpdateCentralPackageVersion(projectPath, packageName, version))
                {
                    return new RunStatus(string.Empty, string.Empty, 0);
                }
            }

            string projectName = _fileSystem.Path.GetFileName(projectPath);

            List<string> arguments = ["add", projectName, "package", packageName, "-v", version.ToString()];
            // File-based apps declare TargetFramework via #:property; dotnet add rejects -f on .cs paths with newer SDKs.
            if (!projectPath.IsCSharpFile())
            {
                arguments.Add("-f");
                arguments.Add(frameworkName);
            }

            if (noRestore)
            {
                arguments.Add("--no-restore");
            }
            if (ignoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            var result = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), [.. arguments]);

            // If the package originally used a variable reference, restore it after the update
            if (result.IsSuccess && variableInfo != null)
            {
                _variableTrackingService.UpdatePackageVariable(variableInfo, version);
            }

            return result;
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
            var projectName = _fileSystem.Path.GetFileName(projectPath);
            string[] arguments = ["remove", projectName, "package", packageName];

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }

        private RunStatus RestoreProject(string projectPath, bool ignoreFailedSources)
        {
            var projectName = _fileSystem.Path.GetFileName(projectPath);
            List<string> arguments = ["restore", projectName];
            if (ignoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), [.. arguments]);
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

        private static RunStatus FileBasedAppUpdateFailed(string projectPath, string packageName, string hint) =>
            new(
                string.Empty,
                $"Failed to update file-based app '{projectPath}' for '{packageName}'. {hint}",
                1);

        private static bool IsFileBasedAppVariableAtRequestedVersion(PackageVariableInfo variableInfo, NuGetVersion version)
        {
            if (NuGetVersion.TryParse(variableInfo.VariableValue, out var current))
            {
                return current == version;
            }

            return string.Equals(variableInfo.VariableValue, version.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFileBasedAppDirectReferenceAtRequestedVersion(FileBasedAppReference reference, NuGetVersion version) =>
            reference.ResolvedVersion == version;
    }
}
