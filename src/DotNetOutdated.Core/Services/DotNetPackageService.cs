using DotNetOutdated.Core;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Upgrades package and SDK references. Where a version is written decides how: literal versions go through
    /// <c>dotnet add package</c> (with <c>Directory.Packages.props</c> formatting kept), and versions that it can't
    /// update or would lose (MSBuild properties, SDK declarations, file-based app directives) are rewritten directly.
    /// </summary>
    internal class DotNetPackageService : IDotNetPackageService
    {
        private readonly Action<string>? _onWarning;
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;
        private readonly VersionLocator _versionLocator;
        private readonly FileBasedAppDirectives _fileBasedAppDirectives;
        private readonly MSBuildVariableWriter _variableWriter;

        // Restores already run for a project after a variable reached a value, so a property shared by several
        // projects (or target frameworks) restores each project once rather than only the first.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _restoredVariableStates =
            new(StringComparer.OrdinalIgnoreCase);

        public DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem, Action<string>? onWarning = null)
            : this(dotNetRunner, fileSystem, new DotNetProjectEvaluator(dotNetRunner, fileSystem), onWarning)
        {
        }

        internal DotNetPackageService(IDotNetRunner dotNetRunner, IFileSystem fileSystem, IProjectEvaluator evaluator, Action<string>? onWarning = null)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
            _onWarning = onWarning;

            _fileBasedAppDirectives = new FileBasedAppDirectives(fileSystem, evaluator, onWarning);
            _versionLocator = new VersionLocator(fileSystem, evaluator, _fileBasedAppDirectives);
            _variableWriter = new MSBuildVariableWriter(fileSystem, onWarning);
        }

        public RunStatus Upgrade(ReferenceUpgrade upgrade)
        {
            ArgumentNullException.ThrowIfNull(upgrade);
            ArgumentNullException.ThrowIfNull(upgrade.Version);
            if (!upgrade.Version.HasLowerBound)
            {
                throw new ArgumentException($"Version range '{upgrade.Version}' must have a lower bound.", nameof(upgrade));
            }

            var noRestore = upgrade.NoRestore;
            var options = new UpgradeOptions(upgrade.ProjectPath, upgrade.Name, upgrade.TargetFramework, upgrade.Version, noRestore, upgrade.IgnoreFailedSources);
            return _versionLocator.Locate(upgrade.ProjectPath, upgrade.Name, upgrade.Kind) switch
            {
                NotEditable notEditable => Failure(notEditable.Message),
                FileBasedVariableVersion location => UpdateFileBasedVariable(location.Variable, options),
                FileBasedDirectiveVersion location => UpdateFileBasedDirective(location.Reference, options),
                ProjectSdkVersion location => UpdateProjectSdk(location.Sdk, options),
                MSBuildVariableVersion location when noRestore || !location.DotNetAddCanUpdate =>
                    UpdateVariable(location.Variable, options),
                MSBuildVariableVersion location => AddWithDotNet(options, location.Variable),
                LiteralVersion location => UpdateLiteral(location, options),
                var location => throw new InvalidOperationException($"Unexpected version location {location}."),
            };
        }

        private sealed record UpgradeOptions(
            string ProjectPath,
            string PackageName,
            string FrameworkName,
            VersionRange VersionRange,
            bool NoRestore,
            bool IgnoreFailedSources)
        {
            // Locations that can only hold a single version (file-based app directives, SDKs) take the lower bound.
            public NuGetVersion Version => VersionRange.MinVersion!; // Upgrade rejects ranges without a lower bound.
        }

        private RunStatus UpdateFileBasedVariable(PackageVariableInfo variableInfo, UpgradeOptions options)
        {
            if (!_fileBasedAppDirectives.TryUpdateVariable(variableInfo, options.Version) &&
                !IsFileBasedAppVariableAtRequestedVersion(variableInfo, options.Version))
            {
                return Failure(
                    $"Failed to update file-based app '{options.ProjectPath}' for '{options.PackageName}'. " +
                    "Could not update the matching #:property value or #:package/#:sdk directive.");
            }

            return RestoreUnlessDisabled(options);
        }

        private RunStatus UpdateFileBasedDirective(FileBasedAppReference reference, UpgradeOptions options)
        {
            if (!_fileBasedAppDirectives.TryUpdateDirectReference(options.ProjectPath, reference.Name, reference.Kind, options.Version) &&
                reference.ResolvedVersion != options.Version)
            {
                return Failure(
                    $"Failed to update file-based app '{options.ProjectPath}' for '{options.PackageName}'. " +
                    $"Expected a literal #:sdk {options.PackageName}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
            }

            return RestoreUnlessDisabled(options);
        }

        // `dotnet add package` cannot update an MSBuild project SDK, so rewrite its version in the project file.
        private RunStatus UpdateProjectSdk(ProjectSdkReference sdk, UpgradeOptions options)
        {
            bool updated;
            try
            {
                updated = ProjectSdkReferenceHelper.TryUpdate(_fileSystem, options.ProjectPath, options.PackageName, options.Version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failure($"Failed to update the version of SDK '{options.PackageName}' in '{options.ProjectPath}': {ex.Message}");
            }

            if (!updated)
            {
                // Already at the requested version: a multi-targeted project gets one call per target
                // framework, and the first call has already rewritten the file and restored.
                return sdk.ResolvedVersion == options.Version
                    ? Success()
                    : Failure($"Failed to update the version of SDK '{options.PackageName}' in '{options.ProjectPath}'.");
            }

            return RestoreUnlessDisabled(options);
        }

        // Writes the variable directly: with --no-restore (avoiding NuGet/Home#12552), or where `dotnet add package`
        // can't edit the reference.
        private RunStatus UpdateVariable(PackageVariableInfo variableInfo, UpgradeOptions options)
        {
            var versionRange = options.VersionRange;
            var updated = _variableWriter.TryUpdate(variableInfo, versionRange);

            // No change is fine when the property already holds the requested value (e.g. the second target
            // framework of a multi-targeted project, or another project sharing the same property).
            if (!updated && !MSBuildVariableWriter.IsAtVersionRange(variableInfo, versionRange))
            {
                return Failure(
                    $"Failed to update '{options.PackageName}': could not write '{versionRange.ToVersionString()}' to the " +
                    $"$({variableInfo.VariableName}) property in '{variableInfo.FilePath}'.");
            }

            if (options.NoRestore)
            {
                return Success();
            }

            var restoreKey = string.Join(
                "|", _fileSystem.Path.GetFullPath(options.ProjectPath), variableInfo.FilePath, variableInfo.VariableName, versionRange.ToVersionString());
            if (!updated && _restoredVariableStates.ContainsKey(restoreKey))
            {
                return Success();
            }

            var restoreStatus = RestoreProject(options);
            if (restoreStatus.IsSuccess)
            {
                _restoredVariableStates[restoreKey] = true;
            }

            return restoreStatus;
        }

        private RunStatus UpdateLiteral(LiteralVersion location, UpgradeOptions options)
        {
            if (location.Warning != null)
            {
                _onWarning?.Invoke(location.Warning);
            }

            // When --no-restore is used, `dotnet add package` has an upstream bug where it writes
            // version info to .csproj instead of Directory.Packages.props for CPM projects.
            // See: https://github.com/NuGet/Home/issues/12552
            if (options.NoRestore)
            {
                switch (TryUpdateCentralPackageVersion(options.ProjectPath, options.PackageName, options.VersionRange, out var centralPackagesPath))
                {
                    case CentralPackageVersionUpdate.Updated:
                        return Success();
                    case CentralPackageVersionUpdate.NoMatchingEntry:
                        // Don't fall back to `dotnet add package --no-restore`: it would write the
                        // version into the project file (NuGet/Home#12552).
                        return Failure(
                            $"Could not update '{options.PackageName}' in '{centralPackagesPath}': no <PackageVersion> or <GlobalPackageReference> " +
                            "element was found with an Include or Update attribute and a Version attribute. Update the version manually.");
                }
            }

            return AddWithDotNet(options, variableInfo: null);
        }

        // Runs `dotnet add package`, then puts back what it loses: the Directory.Packages.props layout and, when the
        // version came from a variable, the variable reference.
        private RunStatus AddWithDotNet(UpgradeOptions options, PackageVariableInfo? variableInfo)
        {
            var projectPath = options.ProjectPath;
            string projectName = _fileSystem.Path.GetFileName(projectPath);

            // The formatting replay writes back exactly the text passed to `dotnet add package -v`.
            var versionText = options.VersionRange.ToVersionString();
            List<string> arguments = ["add", projectName, "package", options.PackageName, "-v", versionText];
            // File-based apps declare TargetFramework via #:property; dotnet add rejects -f on .cs paths with newer SDKs.
            if (!projectPath.IsCSharpFile())
            {
                arguments.Add("-f");
                arguments.Add(options.FrameworkName);
            }

            if (options.NoRestore)
            {
                arguments.Add("--no-restore");
            }
            if (options.IgnoreFailedSources)
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
                    PreserveCentralPackagesFormatting(original.File, original.Bytes, options.PackageName, versionText);
                }

                if (variableInfo != null)
                {
                    _variableWriter.TryUpdate(variableInfo, options.VersionRange);
                }
            }

            return result;
        }

        public RunStatus RemovePackage(string projectPath, string packageName)
        {
            var projectName = _fileSystem.Path.GetFileName(projectPath);
            string[] arguments = ["remove", projectName, "package", packageName];

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }

        private RunStatus RestoreUnlessDisabled(UpgradeOptions options) =>
            options.NoRestore ? Success() : RestoreProject(options);

        private RunStatus RestoreProject(UpgradeOptions options)
        {
            var projectName = _fileSystem.Path.GetFileName(options.ProjectPath);
            List<string> arguments = ["restore", projectName];
            if (options.IgnoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(options.ProjectPath), [.. arguments]);
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

        private CentralPackageVersionUpdate TryUpdateCentralPackageVersion(string projectPath, string packageName, VersionRange versionRange, out string? centralPackagesPath)
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
                IFileInfo? cpvmFile = null;
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

        private static RunStatus Success() => new(string.Empty, string.Empty, 0);

        private static RunStatus Failure(string message) => new(string.Empty, message, 1);

        private static bool IsFileBasedAppVariableAtRequestedVersion(PackageVariableInfo variableInfo, NuGetVersion version)
        {
            if (NuGetVersion.TryParse(variableInfo.VariableValue, out var current))
            {
                return current == version;
            }

            return string.Equals(variableInfo.VariableValue, version.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
