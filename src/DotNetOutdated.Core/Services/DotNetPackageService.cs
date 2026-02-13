using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
            PackageVariableInfo variableInfo = null;
            variables.TryGetValue(packageName, out variableInfo);

            // When --no-restore is used, `dotnet add package` has an upstream bug where it writes
            // version info to .csproj instead of Directory.Packages.props for CPM projects.
            // See: https://github.com/NuGet/Home/issues/12552
            if (noRestore)
            {
                // For CPM projects with a variable reference, update the variable directly.
                // This avoids the dotnet CPM bug AND preserves the variable reference.
                if (variableInfo != null && variableInfo.ElementType != "PackageReference")
                {
                    RestoreVariableReference(projectPath, packageName, variableInfo, version);
                    return new RunStatus(string.Empty, string.Empty, 0);
                }

                // For CPM projects without a variable reference, update Directory.Packages.props directly.
                if (TryUpdateCentralPackageVersion(projectPath, packageName, version))
                {
                    return new RunStatus(string.Empty, string.Empty, 0);
                }
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

            var result = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), [.. arguments]);

            // If the package originally used a variable reference, restore it after the update
            if (result.IsSuccess && variableInfo != null)
            {
                RestoreVariableReference(projectPath, packageName, variableInfo, version);
            }

            return result;
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
           var projectName = _fileSystem.Path.GetFileName(projectPath);
           string[] arguments = ["remove", projectName, "package", packageName];

           return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }

        private void RestoreVariableReference(string projectPath, string packageName, PackageVariableInfo variableInfo, NuGetVersion newVersion)
        {
            try
            {
                string fileToUpdate = variableInfo.FilePath;

                if (!_fileSystem.File.Exists(fileToUpdate))
                {
                    fileToUpdate = projectPath;
                }

                string content = _fileSystem.File.ReadAllText(fileToUpdate);
                var doc = XDocument.Parse(content);

                var propertyElement = doc.Descendants()
                    .Where(e => e.Name.LocalName == variableInfo.VariableName &&
                               e.Parent?.Name.LocalName == "PropertyGroup")
                    .FirstOrDefault();

                if (propertyElement != null)
                {
                    string oldValue = propertyElement.Value;
                    string pattern = $@"<{Regex.Escape(variableInfo.VariableName)}>{Regex.Escape(oldValue)}</{Regex.Escape(variableInfo.VariableName)}>";
                    string replacement = $"<{variableInfo.VariableName}>{newVersion}</{variableInfo.VariableName}>";
                    content = Regex.Replace(content, pattern, replacement);
                }

                var packageElements = doc.Descendants()
                    .Where(e => e.Name.LocalName == variableInfo.ElementType &&
                               (e.Attribute("Include")?.Value.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true ||
                                e.Attribute("Update")?.Value.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true))
                    .ToList();

                foreach (var packageElement in packageElements)
                {
                    var versionAttr = packageElement.Attribute("Version");
                    if (versionAttr != null)
                    {
                        string variableReference = $"$({variableInfo.VariableName})";
                        string packagePattern = $@"(<{Regex.Escape(variableInfo.ElementType)}\s+(?:Include|Update)=""{Regex.Escape(packageName)}""\s+Version="")[^""]*("")";
                        string packageReplacement = $"$1{variableReference}$2";
                        content = Regex.Replace(content, packagePattern, packageReplacement, RegexOptions.IgnoreCase);
                    }
                }

                _fileSystem.File.WriteAllText(fileToUpdate, content);
            }
            catch
            {
                // Silently ignore errors - the package was still upgraded, just without variable reference
            }
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
