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

        // For each version location (a file and the name it holds) this upgrader has written: the value it last wrote
        // there, and the projects restored since. A location shared by several projects, or written by an earlier
        // upgrade (another target framework's), is written once and restores each project once. Writing it again
        // forgets those restores, so a location that was changed back in between restores its projects again.
        private readonly Dictionary<string, WrittenLocation> _writtenLocations = new(StringComparer.OrdinalIgnoreCase);

        private sealed record WrittenLocation(string Value)
        {
            public HashSet<string> RestoredProjects { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

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
                // A held-back project that shares its version location with an upgraded one is upgraded (and
                // restored) with the others: leaving both behind instead could leave a package upgraded for the other
                // target framework depending on a newer version than the one kept, which restore rejects as a
                // downgrade (NU1605).
                var heldBackSharing = HeldBackSharingVersion(upgrade);

                // A project listed once per target framework is upgraded once, for all of those target frameworks.
                return upgrade.Projects
                    .Concat(heldBackSharing)
                    .GroupBy(project => _fileSystem.Path.GetFullPath(project.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(projects => UpgradeProject(
                        upgrade,
                        projects.First().Path,
                        [.. projects.Select(p => p.TargetFramework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                        [.. projects.Where(heldBackSharing.Contains)]))
                    .ToList();
            }
        }

        public IReadOnlyList<UpgradeProject> FindHeldBackSharingVersion(ReferenceUpgrade upgrade)
        {
            ArgumentNullException.ThrowIfNull(upgrade);

            lock (_gate)
            {
                return HeldBackSharingVersion(upgrade);
            }
        }

        // The held-back projects whose package version is written where an upgraded project's is.
        private List<UpgradeProject> HeldBackSharingVersion(ReferenceUpgrade upgrade)
        {
            if (upgrade.HeldBack.Count == 0)
            {
                return [];
            }

            var directives = new FileBasedAppDirectives(_fileSystem, _evaluator, _ => { });
            var locator = new VersionLocator(_fileSystem, _evaluator, directives);
            bool IsPackage(UpgradeProject project) => locator.ResolveKind(project.Path, upgrade.Name, upgrade.Kind) == ReferenceKind.Package;
            string Location(UpgradeProject project) => PackageVersionLocation(project.Path, upgrade.Name, project.TargetFramework);

            var upgradedLocations = upgrade.Projects.Where(IsPackage).Select(Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. upgrade.HeldBack.Where(project => IsPackage(project) && upgradedLocations.Contains(Location(project))).Distinct()];
        }

        // Identifies where a package's version is written for a target framework of a project, so that target
        // frameworks and projects that share it can be told apart from those that don't. A package the project gives
        // no version is added for that target framework alone.
        private string PackageVersionLocation(string projectPath, string name, string? targetFramework)
        {
            var fullPath = _fileSystem.Path.GetFullPath(projectPath);
            if (projectPath.IsCSharpFile())
            {
                return fullPath;
            }

            if (!_evaluator.GetPackageVersions(projectPath, targetFramework).TryGetValue(name, out var declaration))
            {
                return $"{fullPath}|{targetFramework}";
            }

            return declaration.Variable is { UnsupportedReason: null } variable
                ? VariableLocation(variable)
                : $"{_fileSystem.Path.GetFullPath(declaration.FilePath)}|{declaration.ElementType}|{declaration.ElementIndex}";
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

            public IReadOnlyList<UpgradeProject> HeldBackUpgraded { get; init; } = [];
        }

        // What writing a project's version location did.
        private abstract record Write;

        private sealed record WriteFailed(string Message) : Write;

        // The location was written directly with Value; Changed is false when it already held that value.
        // PerTargetFramework is set when another target framework may declare the reference somewhere else (a pin
        // under its own condition), so each target framework's location is written.
        private sealed record Written(bool Changed, string Location, string Value, bool PerTargetFramework = false) : Write;

        // `dotnet add package` changed the version, and restored the project unless --no-restore was set.
        private sealed record AddedWithDotNet(RunStatus Status) : Write;

        // heldBackUpgraded: the held-back target frameworks of this project upgraded with it, as they share a version
        // location with an upgraded project.
        private ProjectUpgradeResult UpgradeProject(ReferenceUpgrade upgrade, string projectPath, IReadOnlyList<string> targetFrameworks, IReadOnlyList<UpgradeProject> heldBackUpgraded)
        {
            var warnings = new List<string>();
            var directives = new FileBasedAppDirectives(_fileSystem, _evaluator, warnings.Add);
            var context = new Context(upgrade, projectPath, warnings, directives, new MSBuildVariableWriter(_fileSystem, warnings.Add))
            {
                HeldBackUpgraded = heldBackUpgraded,
            };
            var locator = new VersionLocator(_fileSystem, _evaluator, directives);
            var kind = locator.ResolveKind(projectPath, upgrade.Name, upgrade.Kind);

            VersionRange? removedRange = null;
            if (kind == ReferenceKind.Package && !IsSdkStyleProject(projectPath))
            {
                // Removing the reference takes its declared range with it, so read that first.
                removedRange = locator.Locate(projectPath, upgrade.Name, kind, targetFrameworks.FirstOrDefault()).DeclaredRange;
                warnings.Add(
                    $"'{projectPath}' isn't an SDK-style project, so {upgrade.Name} was removed and added back at the new version.");
                var removal = RemovePackage(projectPath, upgrade.Name);
                if (!removal.IsSuccess)
                {
                    return Result(context, UpgradeOutcome.Failed, removal.FailureDetails);
                }
            }

            // `dotnet add package -f` changes one target framework's reference, and a pin in an imported file is
            // written on the item that target framework uses, so both run for each target framework the project is
            // listed with (a file-based app takes no -f). Any other location is written once.
            var writes = new List<Write> { WriteLocation(context, locator, kind, removedRange, targetFrameworks.FirstOrDefault()) };
            if (!projectPath.IsCSharpFile())
            {
                foreach (var targetFramework in targetFrameworks.Skip(1))
                {
                    if (writes[^1] is not (AddedWithDotNet { Status.IsSuccess: true } or Written { PerTargetFramework: true }))
                    {
                        break;
                    }

                    writes.Add(WriteLocation(context, locator, kind, removedRange, targetFramework));
                }
            }

            return Finish(context, writes);
        }

        // Finds the version location as it is now (an earlier target framework's `dotnet add package` may have changed
        // it), where this target framework declares it, and writes the new version there.
        private Write WriteLocation(Context context, VersionLocator locator, ReferenceKind kind, VersionRange? removedRange, string? targetFramework)
        {
            var upgrade = context.Upgrade;
            var location = locator.Locate(context.ProjectPath, upgrade.Name, kind, targetFramework);
            var range = RangeFor(location.DeclaredRange ?? removedRange, upgrade.Version);
            return location switch
            {
                NotEditable notEditable => new WriteFailed(notEditable.Message),
                FileBasedVariableVersion l => UpdateFileBasedVariable(context, l.Variable),
                FileBasedDirectiveVersion l => UpdateFileBasedDirective(context, l.Reference),
                ProjectSdkVersion l => UpdateProjectSdk(context, l.Sdk),
                MSBuildVariableVersion l when upgrade.NoRestore || !l.DotNetAddCanUpdate => UpdateVariable(context, l.Variable, range),
                MSBuildVariableVersion l => AddWithDotNet(context, range, l.Variable, targetFramework),
                GlobalPackageReferenceVersion l => UpdateLiteralVersionInFile(context, l.FilePath, "GlobalPackageReference", range),
                PackageReferenceVersionInFile l => UpdateLiteralVersionInFile(context, l.FilePath, "PackageReference", range, l.ElementIndex),
                LiteralVersion l => UpdateLiteral(context, l, range, targetFramework),
                _ => throw new InvalidOperationException($"Unexpected version location {location}."),
            };
        }

        // The writes for each target framework of a project, in order: only the last can have failed.
        private ProjectUpgradeResult Finish(Context context, IReadOnlyList<Write> writes)
        {
            switch (writes[^1])
            {
                case WriteFailed failed:
                    return Result(context, UpgradeOutcome.Failed, failed.Message);

                case AddedWithDotNet { Status.IsSuccess: false } added:
                    return Result(context, UpgradeOutcome.Failed, added.Status.FailureDetails);
            }

            // The locations this upgrade changed, or an earlier one changed to the same value.
            var upgraded = new List<WrittenLocation>();
            foreach (var write in writes)
            {
                switch (write)
                {
                    case AddedWithDotNet:
                        break;

                    case Written { Changed: true } written:
                        // No project has been restored since this write, whatever was restored at the value before.
                        _writtenLocations[written.Location] = new WrittenLocation(written.Value);
                        upgraded.Add(_writtenLocations[written.Location]);
                        break;

                    case Written written when WroteValue(written.Location, written.Value):
                        upgraded.Add(_writtenLocations[written.Location]);
                        break;

                    case Written:
                        break;

                    default:
                        throw new InvalidOperationException($"Unexpected write {write}.");
                }
            }

            if (upgraded.Count == 0)
            {
                return writes.Any(write => write is AddedWithDotNet)
                    ? Result(context, UpgradeOutcome.Upgraded)
                    : Result(context, UpgradeOutcome.AlreadyCurrent);
            }

            var projectFullPath = _fileSystem.Path.GetFullPath(context.ProjectPath);
            if (context.Upgrade.NoRestore || upgraded.All(location => location.RestoredProjects.Contains(projectFullPath)))
            {
                return Result(context, UpgradeOutcome.Upgraded);
            }

            var restore = Restore(context);
            if (!restore.IsSuccess)
            {
                return Result(context, UpgradeOutcome.Failed, restore.FailureDetails);
            }

            foreach (var location in upgraded)
            {
                location.RestoredProjects.Add(projectFullPath);
            }

            return Result(context, UpgradeOutcome.Upgraded);
        }

        // Whether the value at the location is the one this upgrader last wrote there.
        private bool WroteValue(string location, string value) =>
            _writtenLocations.TryGetValue(location, out var written) &&
            string.Equals(written.Value, value, StringComparison.OrdinalIgnoreCase);

        private static ProjectUpgradeResult Result(Context context, UpgradeOutcome outcome, string failureDetails = "") => new()
        {
            ProjectPath = context.ProjectPath,
            Outcome = outcome,
            FailureDetails = outcome == UpgradeOutcome.Failed ? failureDetails : string.Empty,
            Warnings = context.Warnings,
            HeldBackUpgraded = outcome == UpgradeOutcome.Upgraded ? context.HeldBackUpgraded : [],
        };

        private Write UpdateFileBasedVariable(Context context, PackageVariableInfo variableInfo)
        {
            var version = context.Upgrade.Version;
            var location = VariableLocation(variableInfo);
            if (context.Directives.TryUpdateVariable(variableInfo, version))
            {
                return new Written(true, location, version.ToString());
            }

            return IsFileBasedAppVariableAtRequestedVersion(variableInfo, version)
                ? new Written(false, location, version.ToString())
                : new WriteFailed(
                    $"Failed to update file-based app '{context.ProjectPath}' for '{context.Name}'. " +
                    "Could not update the matching #:property value or #:package/#:sdk directive.");
        }

        private Write UpdateFileBasedDirective(Context context, FileBasedAppReference reference)
        {
            var version = context.Upgrade.Version;
            var location = $"{_fileSystem.Path.GetFullPath(context.ProjectPath)}|#:{reference.Kind} {reference.Name}";
            if (context.Directives.TryUpdateDirectReference(context.ProjectPath, reference.Name, reference.Kind, version))
            {
                return new Written(true, location, version.ToString());
            }

            return reference.ResolvedVersion == version
                ? new Written(false, location, version.ToString())
                : new WriteFailed(
                    $"Failed to update file-based app '{context.ProjectPath}' for '{context.Name}'. " +
                    $"Expected a literal #:sdk {context.Name}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
        }

        // `dotnet add package` cannot update an MSBuild project SDK, so rewrite its version in the project file.
        private Write UpdateProjectSdk(Context context, ProjectSdkReference sdk)
        {
            var version = context.Upgrade.Version;
            var location = $"{_fileSystem.Path.GetFullPath(context.ProjectPath)}|Sdk {context.Name}";
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
                return new Written(true, location, version.ToString());
            }

            return sdk.ResolvedVersion == version
                ? new Written(false, location, version.ToString())
                : new WriteFailed($"Failed to update the version of SDK '{context.Name}' in '{context.ProjectPath}'.");
        }

        // Writes the variable directly: with --no-restore (avoiding NuGet/Home#12552), or where `dotnet add package`
        // can't edit the reference.
        private static Write UpdateVariable(Context context, PackageVariableInfo variableInfo, VersionRange range)
        {
            var location = VariableLocation(variableInfo);
            var versionText = range.ToVersionString();
            if (context.VariableWriter.TryUpdate(variableInfo, range))
            {
                return new Written(true, location, versionText);
            }

            return MSBuildVariableWriter.IsAtVersionRange(variableInfo, range)
                ? new Written(false, location, versionText)
                : new WriteFailed(
                    $"Failed to update '{context.Name}': could not write '{range.ToVersionString()}' to the " +
                    $"$({variableInfo.VariableName}) property in '{variableInfo.FilePath}'.");
        }

        private static string VariableLocation(PackageVariableInfo variableInfo) =>
            $"{variableInfo.FilePath}|$({variableInfo.VariableName})";

        // Writes a literal version directly in the file that declares it: for a GlobalPackageReference `dotnet add package`
        // would add a PackageReference instead, which central package management rejects; for a PackageReference in an
        // imported file (such as a shared Directory.Build.props) or an Update item it fails with "Cannot edit items in
        // imported files". With an element index only that element is written, so the file's other pins of the package
        // (under another target framework's condition, say) are kept.
        private Write UpdateLiteralVersionInFile(Context context, string filePath, string elementName, VersionRange range, int elementIndex = -1)
        {
            var versionText = range.ToVersionString();
            var file = EncodedText.Read(_fileSystem, filePath);
            bool matched;
            var newContent = elementIndex >= 0
                ? PackageVersionMarkup.ReplaceVersionAt(file.Content, elementName, context.Name, elementIndex, versionText, out matched)
                : PackageVersionMarkup.ReplaceVersion(file.Content, [elementName], context.Name, versionText, out matched);

            if (!matched)
            {
                return new WriteFailed(
                    $"Could not update '{context.Name}' in '{filePath}': no <{elementName}> element was found " +
                    "with an Include or Update attribute and a literal version (a Version attribute or <Version> child element). Update the version manually.");
            }

            var updated = newContent != file.Content;
            if (updated)
            {
                // Keep the original encoding and byte order mark.
                file.Write(_fileSystem, filePath, newContent);
            }

            return elementIndex >= 0
                ? new Written(updated, $"{filePath}|{context.Name}|{elementIndex}", versionText, PerTargetFramework: true)
                : new Written(updated, $"{filePath}|{context.Name}", versionText);
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
                    "element was found with an Include or Update attribute and a version (a Version attribute or <Version> child element). Update the version manually.");
            }

            var updated = newContent != file.Content;
            if (updated)
            {
                // Keep the original encoding and byte order mark.
                file.Write(_fileSystem, centralPackagesPath, newContent);
            }

            return new Written(updated, $"{centralPackagesPath}|{context.Name}", versionText);
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
                    var location = VariableLocation(variableInfo);
                    var variableChanges = !MSBuildVariableWriter.IsAtVersionRange(variableInfo, range);
                    context.VariableWriter.TryUpdate(variableInfo, range);

                    // A project that shares the variable but has it written directly then finds it written by this
                    // upgrader, not already current, and is restored.
                    if (variableChanges || !WroteValue(location, versionText))
                    {
                        _writtenLocations[location] = new WrittenLocation(versionText);
                    }
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
