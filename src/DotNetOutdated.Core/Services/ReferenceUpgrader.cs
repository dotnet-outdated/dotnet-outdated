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
    internal sealed class ReferenceUpgrader : IReferenceUpgrader
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;
        private readonly IProjectEvaluator _evaluator;

        // Files are edited in place, so one upgrade runs at a time.
        private readonly object _gate = new();

        // Each version location (a file, the name it holds and the value written) this upgrader has written, and each
        // project restored after one reached its value. A location shared by several projects, or written by an
        // earlier upgrade (another target framework's), is written once and restores each project once.
        private readonly HashSet<string> _writtenLocations = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _restoredProjects = new(StringComparer.OrdinalIgnoreCase);

        public ReferenceUpgrader(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
            : this(dotNetRunner, fileSystem, new DotNetProjectEvaluator(dotNetRunner, fileSystem))
        {
        }

        internal ReferenceUpgrader(IDotNetRunner dotNetRunner, IFileSystem fileSystem, IProjectEvaluator evaluator)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
            _evaluator = evaluator;
        }

        public IReadOnlyList<ProjectUpgradeResult> Upgrade(ReferenceUpgrade upgrade)
        {
            ArgumentNullException.ThrowIfNull(upgrade);
            ArgumentNullException.ThrowIfNull(upgrade.Version);

            lock (_gate)
            {
                // A project listed once per target framework is upgraded once, for all of those target frameworks.
                return upgrade.Projects
                    .GroupBy(project => _fileSystem.Path.GetFullPath(project.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(projects => UpgradeProject(
                        upgrade,
                        projects.First().Path,
                        [.. projects.Select(p => p.TargetFramework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)]))
                    .ToList();
            }
        }

        /// <summary>
        /// The range to write: <paramref name="version"/>, keeping the upper bound of a declared range that it still
        /// satisfies. A floating version isn't kept, and a range the new version is outside of is never written.
        /// </summary>
        internal static VersionRange RangeFor(VersionRange? declared, NuGetVersion version) =>
            declared is { HasUpperBound: true } && declared.Satisfies(version)
                ? new VersionRange(version, includeMinVersion: true, declared.MaxVersion, declared.IsMaxInclusive)
                : new VersionRange(version);

        // What upgrading one project needs: the request, and the modules that collect this project's warnings.
        private sealed record Context(
            ReferenceUpgrade Upgrade,
            string ProjectPath,
            List<string> Warnings,
            FileBasedAppDirectives Directives,
            MSBuildVariableWriter VariableWriter)
        {
            public string Name => Upgrade.Name;
        }

        // What writing a project's version location did.
        private abstract record Write;

        private sealed record WriteFailed(string Message) : Write;

        // The location was written directly; Changed is false when it already held the value.
        private sealed record Written(bool Changed, string Location) : Write;

        // `dotnet add package` changed the version, and restored the project unless --no-restore was set.
        private sealed record AddedWithDotNet(RunStatus Status) : Write;

        private ProjectUpgradeResult UpgradeProject(ReferenceUpgrade upgrade, string projectPath, IReadOnlyList<string> targetFrameworks)
        {
            var warnings = new List<string>();
            var directives = new FileBasedAppDirectives(_fileSystem, _evaluator, warnings.Add);
            var context = new Context(upgrade, projectPath, warnings, directives, new MSBuildVariableWriter(_fileSystem, warnings.Add));
            var locator = new VersionLocator(_fileSystem, _evaluator, directives);
            var kind = locator.ResolveKind(projectPath, upgrade.Name, upgrade.Kind);

            VersionRange? removedRange = null;
            if (kind == ReferenceKind.Package && !IsSdkStyleProject(projectPath))
            {
                // Removing the reference takes its declared range with it, so read that first.
                removedRange = locator.Locate(projectPath, upgrade.Name, kind).DeclaredRange;
                warnings.Add(
                    $"'{projectPath}' isn't an SDK-style project, so {upgrade.Name} was removed and added back at the new version.");
                var removal = RemovePackage(projectPath, upgrade.Name);
                if (!removal.IsSuccess)
                {
                    return Result(context, UpgradeOutcome.Failed, removal.FailureDetails);
                }
            }

            // `dotnet add package -f` changes one target framework's reference, so it runs for each target framework
            // the project is listed with (a file-based app takes no -f). Any other location is written once.
            var write = WriteLocation(context, locator, kind, removedRange, targetFrameworks.FirstOrDefault());
            if (!projectPath.IsCSharpFile())
            {
                foreach (var targetFramework in targetFrameworks.Skip(1))
                {
                    if (write is not AddedWithDotNet { Status.IsSuccess: true })
                    {
                        break;
                    }

                    write = WriteLocation(context, locator, kind, removedRange, targetFramework);
                }
            }

            return Finish(context, write);
        }

        // Finds the version location as it is now (an earlier target framework's `dotnet add package` may have changed
        // it) and writes the new version there.
        private Write WriteLocation(Context context, VersionLocator locator, ReferenceKind kind, VersionRange? removedRange, string? targetFramework)
        {
            var upgrade = context.Upgrade;
            var location = locator.Locate(context.ProjectPath, upgrade.Name, kind);
            var range = RangeFor(location.DeclaredRange ?? removedRange, upgrade.Version);
            return location switch
            {
                NotEditable notEditable => new WriteFailed(notEditable.Message),
                FileBasedVariableVersion l => UpdateFileBasedVariable(context, l.Variable),
                FileBasedDirectiveVersion l => UpdateFileBasedDirective(context, l.Reference),
                ProjectSdkVersion l => UpdateProjectSdk(context, l.Sdk),
                MSBuildVariableVersion l when upgrade.NoRestore || !l.DotNetAddCanUpdate => UpdateVariable(context, l.Variable, range),
                MSBuildVariableVersion l => AddWithDotNet(context, range, l.Variable, targetFramework),
                GlobalPackageReferenceVersion l => UpdateGlobalPackageReference(context, l.FilePath, range),
                LiteralVersion l => UpdateLiteral(context, l, range, targetFramework),
                _ => throw new InvalidOperationException($"Unexpected version location {location}."),
            };
        }

        private ProjectUpgradeResult Finish(Context context, Write write)
        {
            switch (write)
            {
                case WriteFailed failed:
                    return Result(context, UpgradeOutcome.Failed, failed.Message);

                case AddedWithDotNet added:
                    return added.Status.IsSuccess
                        ? Result(context, UpgradeOutcome.Upgraded)
                        : Result(context, UpgradeOutcome.Failed, added.Status.FailureDetails);

                case Written written:
                    if (written.Changed)
                    {
                        _writtenLocations.Add(written.Location);
                    }
                    else if (!_writtenLocations.Contains(written.Location))
                    {
                        return Result(context, UpgradeOutcome.AlreadyCurrent);
                    }

                    var restoreKey = $"{_fileSystem.Path.GetFullPath(context.ProjectPath)}|{written.Location}";
                    if (context.Upgrade.NoRestore || _restoredProjects.Contains(restoreKey))
                    {
                        return Result(context, UpgradeOutcome.Upgraded);
                    }

                    var restore = Restore(context);
                    if (!restore.IsSuccess)
                    {
                        return Result(context, UpgradeOutcome.Failed, restore.FailureDetails);
                    }

                    _restoredProjects.Add(restoreKey);
                    return Result(context, UpgradeOutcome.Upgraded);

                default:
                    throw new InvalidOperationException($"Unexpected write {write}.");
            }
        }

        private static ProjectUpgradeResult Result(Context context, UpgradeOutcome outcome, string failureDetails = "") => new()
        {
            ProjectPath = context.ProjectPath,
            Outcome = outcome,
            FailureDetails = outcome == UpgradeOutcome.Failed ? failureDetails : string.Empty,
            Warnings = context.Warnings,
        };

        private Write UpdateFileBasedVariable(Context context, PackageVariableInfo variableInfo)
        {
            var version = context.Upgrade.Version;
            var location = $"{variableInfo.FilePath}|$({variableInfo.VariableName})|{version}";
            if (context.Directives.TryUpdateVariable(variableInfo, version))
            {
                return new Written(true, location);
            }

            return IsFileBasedAppVariableAtRequestedVersion(variableInfo, version)
                ? new Written(false, location)
                : new WriteFailed(
                    $"Failed to update file-based app '{context.ProjectPath}' for '{context.Name}'. " +
                    "Could not update the matching #:property value or #:package/#:sdk directive.");
        }

        private Write UpdateFileBasedDirective(Context context, FileBasedAppReference reference)
        {
            var version = context.Upgrade.Version;
            var location = $"{_fileSystem.Path.GetFullPath(context.ProjectPath)}|#:{reference.Kind} {reference.Name}|{version}";
            if (context.Directives.TryUpdateDirectReference(context.ProjectPath, reference.Name, reference.Kind, version))
            {
                return new Written(true, location);
            }

            return reference.ResolvedVersion == version
                ? new Written(false, location)
                : new WriteFailed(
                    $"Failed to update file-based app '{context.ProjectPath}' for '{context.Name}'. " +
                    $"Expected a literal #:sdk {context.Name}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
        }

        // `dotnet add package` cannot update an MSBuild project SDK, so rewrite its version in the project file.
        private Write UpdateProjectSdk(Context context, ProjectSdkReference sdk)
        {
            var version = context.Upgrade.Version;
            var location = $"{_fileSystem.Path.GetFullPath(context.ProjectPath)}|Sdk {context.Name}|{version}";
            bool updated;
            try
            {
                updated = ProjectSdkReferenceHelper.TryUpdate(_fileSystem, context.ProjectPath, context.Name, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new WriteFailed($"Failed to update the version of SDK '{context.Name}' in '{context.ProjectPath}': {ex.Message}");
            }

            if (updated)
            {
                return new Written(true, location);
            }

            return sdk.ResolvedVersion == version
                ? new Written(false, location)
                : new WriteFailed($"Failed to update the version of SDK '{context.Name}' in '{context.ProjectPath}'.");
        }

        // Writes the variable directly: with --no-restore (avoiding NuGet/Home#12552), or where `dotnet add package`
        // can't edit the reference.
        private static Write UpdateVariable(Context context, PackageVariableInfo variableInfo, VersionRange range)
        {
            var location = VariableLocation(variableInfo, range);
            if (context.VariableWriter.TryUpdate(variableInfo, range))
            {
                return new Written(true, location);
            }

            return MSBuildVariableWriter.IsAtVersionRange(variableInfo, range)
                ? new Written(false, location)
                : new WriteFailed(
                    $"Failed to update '{context.Name}': could not write '{range.ToVersionString()}' to the " +
                    $"$({variableInfo.VariableName}) property in '{variableInfo.FilePath}'.");
        }

        private static string VariableLocation(PackageVariableInfo variableInfo, VersionRange range) =>
            $"{variableInfo.FilePath}|$({variableInfo.VariableName})|{range.ToVersionString()}";

        // Writes a GlobalPackageReference's literal version directly: `dotnet add package` would add a PackageReference
        // instead, which central package management rejects.
        private Write UpdateGlobalPackageReference(Context context, string filePath, VersionRange range)
        {
            var versionText = range.ToVersionString();
            var file = EncodedText.Read(_fileSystem, filePath);
            var newContent = PackageVersionMarkup.ReplaceVersion(
                file.Content, ["GlobalPackageReference"], context.Name, versionText, out var matched);

            if (!matched)
            {
                return new WriteFailed(
                    $"Could not update '{context.Name}' in '{filePath}': no <GlobalPackageReference> element was found " +
                    "with an Include or Update attribute and a literal Version attribute. Update the version manually.");
            }

            var updated = newContent != file.Content;
            if (updated)
            {
                // Keep the original encoding and byte order mark.
                file.Write(_fileSystem, filePath, newContent);
            }

            return new Written(updated, $"{filePath}|{context.Name}|{versionText}");
        }

        private Write UpdateLiteral(Context context, LiteralVersion location, VersionRange range, string? targetFramework)
        {
            if (location.Warning != null && !context.Warnings.Contains(location.Warning))
            {
                context.Warnings.Add(location.Warning);
            }

            // When --no-restore is used, `dotnet add package` has an upstream bug where it writes
            // version info to .csproj instead of Directory.Packages.props for CPM projects, so write the central
            // version directly. See: https://github.com/NuGet/Home/issues/12552
            if (context.Upgrade.NoRestore && location.Declaration is { ElementType: PackageVersionDeclaration.PackageVersion } central)
            {
                return UpdateCentralPackageVersion(context, central.FilePath, range);
            }

            return AddWithDotNet(context, range, variableInfo: null, targetFramework);
        }

        private Write UpdateCentralPackageVersion(Context context, string centralPackagesPath, VersionRange range)
        {
            var versionText = range.ToVersionString();
            var file = EncodedText.Read(_fileSystem, centralPackagesPath);
            var newContent = PackageVersionMarkup.ReplaceVersion(
                file.Content, PackageVersionMarkup.CentralPackageElements, context.Name, versionText, out var matched);

            if (!matched)
            {
                // Don't fall back to `dotnet add package --no-restore`: it would write the version into the project
                // file (NuGet/Home#12552).
                return new WriteFailed(
                    $"Could not update '{context.Name}' in '{centralPackagesPath}': no <PackageVersion> or <GlobalPackageReference> " +
                    "element was found with an Include or Update attribute and a Version attribute. Update the version manually.");
            }

            var updated = newContent != file.Content;
            if (updated)
            {
                // Keep the original encoding and byte order mark.
                file.Write(_fileSystem, centralPackagesPath, newContent);
            }

            return new Written(updated, $"{centralPackagesPath}|{context.Name}|{versionText}");
        }

        // Runs `dotnet add package`, then puts back what it loses: the Directory.Packages.props layout and, when the
        // version came from a variable, the variable reference.
        private AddedWithDotNet AddWithDotNet(Context context, VersionRange range, PackageVariableInfo? variableInfo, string? targetFramework)
        {
            var projectPath = context.ProjectPath;
            string projectName = _fileSystem.Path.GetFileName(projectPath);

            // The formatting replay writes back exactly the text passed to `dotnet add package -v`.
            var versionText = range.ToVersionString();
            List<string> arguments = ["add", projectName, "package", context.Name, "-v", versionText];
            // File-based apps declare TargetFramework via #:property; dotnet add rejects -f on .cs paths with newer SDKs.
            if (!projectPath.IsCSharpFile() && targetFramework != null)
            {
                arguments.Add("-f");
                arguments.Add(targetFramework);
            }

            if (context.Upgrade.NoRestore)
            {
                arguments.Add("--no-restore");
            }
            if (context.Upgrade.IgnoreFailedSources)
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
                    PreserveCentralPackagesFormatting(original.File, original.Bytes, context.Name, versionText);
                }

                if (variableInfo != null)
                {
                    context.VariableWriter.TryUpdate(variableInfo, range);

                    // A project that shares the variable but has it written directly then finds it written by this
                    // upgrader, not already current, and is restored.
                    _writtenLocations.Add(VariableLocation(variableInfo, range));
                }
            }

            return new AddedWithDotNet(result);
        }

        private RunStatus RemovePackage(string projectPath, string packageName)
        {
            var projectName = _fileSystem.Path.GetFileName(projectPath);
            string[] arguments = ["remove", projectName, "package", packageName];

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), arguments);
        }

        private RunStatus Restore(Context context)
        {
            var projectName = _fileSystem.Path.GetFileName(context.ProjectPath);
            List<string> arguments = ["restore", projectName];
            if (context.Upgrade.IgnoreFailedSources)
            {
                arguments.Add("--ignore-failed-sources");
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(context.ProjectPath), [.. arguments]);
        }

        // `dotnet add package` can't update a reference in a project that isn't SDK-style, so such a project has the
        // package removed and added back. A file that can't be read is left to the upgrade to report.
        private bool IsSdkStyleProject(string projectPath)
        {
            // A file-based app (a .cs file) has no project file to be anything but SDK-style.
            if (projectPath.IsCSharpFile())
            {
                return true;
            }

            XDocument xml;
            try
            {
                xml = XDocument.Parse(_fileSystem.File.ReadAllText(projectPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
            catch (XmlException)
            {
                return false;
            }

            if (xml.Root == null)
            {
                return false;
            }

            // If the project file declares the xmlns attribute, we need to account for it in queries for elements.
            // e.g. <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" Sdk="Microsoft.NET.Sdk">
            XNamespace ns = xml.Root.GetDefaultNamespace();
            // SDKs can also be declared with child elements, e.g. <Sdk Name="Microsoft.Build.NoTargets" Version="3.7.0" />
            // or <Import Project="Sdk.props" Sdk="Microsoft.Build.NoTargets" Version="3.7.0" />
            return xml.Root.Name == (ns + "Project") &&
                   (!string.IsNullOrEmpty(xml.Root.Attribute("Sdk")?.Value) ||
                    xml.Root.Elements(ns + "Sdk").Any() ||
                    xml.Root.Descendants(ns + "Import").Any(import => !string.IsNullOrEmpty(import.Attribute("Sdk")?.Value)));
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
