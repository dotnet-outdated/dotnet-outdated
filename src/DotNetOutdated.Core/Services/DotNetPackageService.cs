using DotNetOutdated.Core;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
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

            return AddPackage(projectPath, packageName, frameworkName, new VersionRange(version), noRestore, ignoreFailedSources);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange)
        {
            return AddPackage(projectPath, packageName, frameworkName, versionRange, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, VersionRange versionRange, bool noRestore, bool ignoreFailedSources = false)
        {
            ArgumentNullException.ThrowIfNull(versionRange);
            if (!versionRange.HasLowerBound)
            {
                throw new ArgumentException($"Version range '{versionRange}' must have a lower bound.", nameof(versionRange));
            }

            // Paths that can only express a single version (file-based app variables, #:sdk) use the lower bound.
            var version = versionRange.MinVersion;

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
            else
            {
                // `dotnet add package` cannot update an MSBuild project SDK, so rewrite its version in the project file.
                var sdkReference = ProjectSdkReferenceHelper.Discover(_fileSystem, projectPath)
                    .FirstOrDefault(reference => string.Equals(reference.Name, packageName, StringComparison.OrdinalIgnoreCase));

                if (sdkReference != null)
                {
                    bool updated;
                    try
                    {
                        updated = ProjectSdkReferenceHelper.TryUpdate(_fileSystem, projectPath, packageName, version);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new RunStatus(
                            string.Empty,
                            $"Failed to update the version of SDK '{packageName}' in '{projectPath}': {ex.Message}",
                            1);
                    }

                    if (!updated)
                    {
                        // Already at the requested version: a multi-targeted project gets one call per target
                        // framework, and the first call has already rewritten the file and restored.
                        return sdkReference.ResolvedVersion == version
                            ? new RunStatus(string.Empty, string.Empty, 0)
                            : new RunStatus(
                                string.Empty,
                                $"Failed to update the version of SDK '{packageName}' in '{projectPath}'.",
                                1);
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
                    // No change is fine when the property already holds the requested value
                    // (e.g. the second target framework of a multi-targeted project).
                    if (!_variableTrackingService.TryUpdatePackageVariable(variableInfo, versionRange) &&
                        !IsVariableAtRequestedVersionRange(variableInfo, versionRange))
                    {
                        return new RunStatus(
                            string.Empty,
                            $"Failed to update '{packageName}': could not write '{versionRange.ToVersionString()}' to the " +
                            $"$({variableInfo.VariableName}) property in '{variableInfo.FilePath}'.",
                            1);
                    }

                    return new RunStatus(string.Empty, string.Empty, 0);
                }

                // For CPM projects without a variable reference, update Directory.Packages.props directly.
                switch (TryUpdateCentralPackageVersion(projectPath, packageName, versionRange, out var centralPackagesPath))
                {
                    case CentralPackageVersionUpdate.Updated:
                        return new RunStatus(string.Empty, string.Empty, 0);
                    case CentralPackageVersionUpdate.NoMatchingEntry:
                        // Don't fall back to `dotnet add package --no-restore`: it would write the
                        // version into the project file (NuGet/Home#12552).
                        return new RunStatus(
                            string.Empty,
                            $"Could not update '{packageName}' in '{centralPackagesPath}': no <PackageVersion> or <GlobalPackageReference> " +
                            "element was found with an Include or Update attribute and a Version attribute. Update the version manually.",
                            1);
                }
            }

            string projectName = _fileSystem.Path.GetFileName(projectPath);

            // The formatting replay writes back exactly the text passed to `dotnet add package -v`.
            var versionText = versionRange.ToVersionString();
            List<string> arguments = ["add", projectName, "package", packageName, "-v", versionText];
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

            var originalCentralPackages = FindCentralPackagesFiles(projectPath)
                .Select(file => (File: file, Bytes: _fileSystem.File.ReadAllBytes(file.FullName)))
                .ToList();

            var result = _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), [.. arguments]);

            if (result.IsSuccess)
            {
                foreach (var original in originalCentralPackages)
                {
                    PreserveCentralPackagesFormatting(original.File, original.Bytes, packageName, versionText);
                }
            }

            // If the package originally used a variable reference, restore it after the update
            if (result.IsSuccess && variableInfo != null)
            {
                _variableTrackingService.UpdatePackageVariable(variableInfo, versionRange);
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

        private enum CentralPackageVersionUpdate
        {
            /// <summary>No Directory.Packages.props mentions the package.</summary>
            NotFound,

            /// <summary>The entry was found and now has the requested version.</summary>
            Updated,

            /// <summary>The nearest Directory.Packages.props mentions the package, but no entry could be updated.</summary>
            NoMatchingEntry,
        }

        private CentralPackageVersionUpdate TryUpdateCentralPackageVersion(string projectPath, string packageName, VersionRange versionRange, out string centralPackagesPath)
        {
            centralPackagesPath = null;
            foreach (var cpvmFile in FindCentralPackagesFiles(projectPath))
            {
                var file = EncodedText.Read(_fileSystem, cpvmFile.FullName);
                var fileContent = file.Content;
                if (!fileContent.Contains($"\"{packageName}\"", StringComparison.OrdinalIgnoreCase) &&
                    !fileContent.Contains($"'{packageName}'", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                centralPackagesPath = cpvmFile.FullName;
                var newFileContent = PackageVersionMarkup.ReplaceVersion(
                    fileContent, PackageVersionMarkup.CentralPackageElements, packageName, versionRange.ToVersionString(), out var matched);

                if (!matched)
                {
                    return CentralPackageVersionUpdate.NoMatchingEntry;
                }

                if (newFileContent != fileContent)
                {
                    // Keep the original encoding and byte order mark.
                    file.Write(_fileSystem, cpvmFile.FullName, newFileContent);
                }

                return CentralPackageVersionUpdate.Updated;
            }

            return CentralPackageVersionUpdate.NotFound;
        }

        private IEnumerable<IFileInfo> FindCentralPackagesFiles(string projectPath)
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
                    yield return cpvmFile;
                }

                directory = directory.Parent;
            }
        }

        private void PreserveCentralPackagesFormatting(IFileInfo file, byte[] originalBytes, string packageName, string versionText)
        {
            if (!_fileSystem.File.Exists(file.FullName))
            {
                return;
            }

            var updatedBytes = _fileSystem.File.ReadAllBytes(file.FullName);
            if (originalBytes.AsSpan().SequenceEqual(updatedBytes))
            {
                return;
            }

            var original = EncodedText.TryDecode(originalBytes);
            if (original == null)
            {
                // Do not replay content that could not be decoded without losing bytes.
                return;
            }

            var originalContent = original.Content;

            var formattedContent = PackageVersionMarkup.ReplaceVersion(
                originalContent, PackageVersionMarkup.CentralPackageElements, packageName, versionText);
            var updatedContent = _fileSystem.File.ReadAllText(file.FullName);

            try
            {
                // Keep dotnet add's validation and any additional XML changes. Reuse the original
                // layout only when changing the version produces the same document as the CLI.
                if (formattedContent != updatedContent &&
                    !XNode.DeepEquals(XDocument.Parse(formattedContent), XDocument.Parse(updatedContent)))
                {
                    return;
                }
            }
            catch (XmlException)
            {
                return;
            }

            var formattedBytes = original.Encode(formattedContent);
            if (!formattedBytes.AsSpan().SequenceEqual(updatedBytes))
            {
                _fileSystem.File.WriteAllBytes(file.FullName, formattedBytes);
            }
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

        private static bool IsVariableAtRequestedVersionRange(PackageVariableInfo variableInfo, VersionRange versionRange) =>
            VersionRange.TryParse(variableInfo.VariableValue, out var current) && current.Equals(versionRange);

        private static bool IsFileBasedAppDirectReferenceAtRequestedVersion(FileBasedAppReference reference, NuGetVersion version) =>
            reference.ResolvedVersion == version;
    }
}
