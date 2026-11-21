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
        private readonly Dictionary<string, WrittenLocation> _writtenLocations = new(LocationComparer);

        private sealed record WrittenLocation(string Value)
        {
            public HashSet<string> RestoredProjects { get; } = new(LocationComparer);
        }

        // Locations (see Location) and project paths spell each file's path as the file system stores it, so they
        // compare ordinally: two paths that differ only in case are one file where the volume ignores case, and two
        // where it doesn't.
        private static readonly StringComparer LocationComparer = StringComparer.Ordinal;

        // A version location: the file, and what in it holds the version. Ids and property names are case-insensitive.
        private string Location(string filePath, string what) => $"{PathCase.OnDisk(_fileSystem, filePath)}|{what.ToUpperInvariant()}";

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
                var plan = PlanSharing(upgrade);
                var heldBackSharing = plan.HeldBack.Select(entry => entry.Project).ToHashSet();
                var requested = upgrade.Projects.ToHashSet();

                // A project listed once per target framework is upgraded once, for all of those target frameworks.
                // Projects are told apart by the path the file system stores, so two whose paths differ only in case
                // are one project where the volume ignores case and two where it doesn't. Held-back projects come
                // after the projects they share a location with, in the order they were found.
                var results = new List<ProjectUpgradeResult>();
                _reachedLocations = new HashSet<string>(LocationComparer);
                try
                {
                    foreach (var projects in upgrade.Projects
                        .Concat(plan.HeldBack.Select(entry => entry.Project))
                        .GroupBy(project => PathCase.OnDisk(_fileSystem, project.Path), StringComparer.Ordinal))
                    {
                        // A held-back project is upgraded because an earlier one writes a location it shares; if none
                        // of those got as far as writing (its write was refused, say), it keeps its version.
                        var locations = projects.SelectMany(project => plan.Locations.GetValueOrDefault(project, [])).ToList();
                        if (!projects.Any(requested.Contains) && !locations.Any(_reachedLocations.Contains))
                        {
                            results.Add(new ProjectUpgradeResult
                            {
                                ProjectPath = projects.First().Path,
                                Outcome = UpgradeOutcome.Failed,
                                FailureDetails =
                                    $"'{projects.First().Path}' was held back but shares the version of {upgrade.Name} with a project that couldn't be " +
                                    "upgraded, so neither was changed.",
                            });
                            continue;
                        }

                        var result = UpgradeProject(
                            upgrade,
                            projects.First().Path,
                            [.. projects.Select(p => p.TargetFramework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                            [.. projects.Where(heldBackSharing.Contains)]);
                        if (result.Outcome != UpgradeOutcome.Failed)
                        {
                            _reachedLocations.UnionWith(locations);
                        }

                        results.Add(result);
                    }
                }
                finally
                {
                    _reachedLocations = null;
                }

                return results;
            }
        }

        public IReadOnlyList<UpgradeProject> FindHeldBackSharingVersion(ReferenceUpgrade upgrade)
        {
            ArgumentNullException.ThrowIfNull(upgrade);

            lock (_gate)
            {
                var sharing = PlanSharing(upgrade).HeldBack.Select(entry => entry.Project).ToHashSet();
                return [.. upgrade.HeldBack.Distinct().Where(sharing.Contains)];
            }
        }

        // The held-back projects whose package version is written where an upgraded project's is, in the order they
        // were found, and each project's version locations.
        private sealed record SharingPlan(
            IReadOnlyList<(UpgradeProject Project, IReadOnlyList<string> Locations)> HeldBack,
            IReadOnlyDictionary<UpgradeProject, IReadOnlyList<string>> Locations);

        // The locations the projects upgraded so far in Upgrade wrote (or found already at the new value), or null
        // outside Upgrade.
        private HashSet<string>? _reachedLocations;

        // Upgrading a project writes all of its locations, so a held-back project sharing one of them is upgraded
        // too, and so is one sharing another of that one's, and so on.
        private SharingPlan PlanSharing(ReferenceUpgrade upgrade)
        {
            if (upgrade.HeldBack.Count == 0)
            {
                return new SharingPlan([], new Dictionary<UpgradeProject, IReadOnlyList<string>>());
            }

            var directives = new FileBasedAppDirectives(_fileSystem, _evaluator, _ => { });
            var locator = new VersionLocator(_fileSystem, _evaluator, directives);
            bool IsPackage(UpgradeProject project) => locator.ResolveKind(project.Path, upgrade.Name, upgrade.Kind) == ReferenceKind.Package;
            IReadOnlyList<string> Locations(UpgradeProject project) => PackageVersionLocations(directives, project.Path, upgrade.Name, project.TargetFramework);

            var projectLocations = new Dictionary<UpgradeProject, IReadOnlyList<string>>();
            foreach (var project in upgrade.Projects.Distinct().Where(IsPackage))
            {
                projectLocations[project] = Locations(project);
            }

            var writtenLocations = projectLocations.Values.SelectMany(locations => locations).ToHashSet(LocationComparer);
            var heldBack = upgrade.HeldBack.Distinct().Where(IsPackage).Select(project => (Project: project, Locations: Locations(project))).ToList();
            var sharing = new List<(UpgradeProject Project, IReadOnlyList<string> Locations)>();
            bool found;
            do
            {
                found = false;
                foreach (var entry in heldBack)
                {
                    if (!sharing.Contains(entry) && entry.Locations.Any(writtenLocations.Contains))
                    {
                        sharing.Add(entry);
                        projectLocations.TryAdd(entry.Project, entry.Locations);
                        writtenLocations.UnionWith(entry.Locations);
                        found = true;
                    }
                }
            }
            while (found);

            return new SharingPlan(sharing, projectLocations);
        }

        // Identifies where a package's version is written for a target framework of a project, so that target
        // frameworks and projects that share it can be told apart from those that don't. A package the project gives
        // no version is added for that target framework alone. A file-based app can declare it in several of the files
        // it compiles, which other apps may include too.
        private IReadOnlyList<string> PackageVersionLocations(FileBasedAppDirectives directives, string projectPath, string name, string? targetFramework)
        {
            var fullPath = _fileSystem.Path.GetFullPath(projectPath);
            if (projectPath.IsCSharpFile())
            {
                if (directives.PackageVariables(projectPath, ReferenceKind.Package).TryGetValue(name, out var directiveVariable))
                {
                    return [.. FileBasedVariableLocations(directives, projectPath, directiveVariable).Select(location => location.Location)];
                }

                var declaringFiles = directives.DeclaringFiles(projectPath, ReferenceKind.Package, name);
                return declaringFiles.Count > 0
                    ? [.. declaringFiles.Select(file => DirectiveLocation(file, ReferenceKind.Package, name))]
                    : [fullPath];
            }

            if (!_evaluator.GetPackageVersions(projectPath, targetFramework).TryGetValue(name, out var declaration))
            {
                return [Location(fullPath, targetFramework ?? string.Empty)];
            }

            return declaration.Variable is { UnsupportedReason: null } variable
                ? [VariableLocation(variable)]
                : [Location(_fileSystem.Path.GetFullPath(declaration.FilePath), $"{declaration.ElementType}|{declaration.ElementIndex}")];
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

        // The locations were written directly with Value; a location's Changed is false when it already held that
        // value. A reference declared in several files (an app's directive repeated in a file it includes) has a
        // location for each. PerTargetFramework is set when another target framework may declare the reference
        // somewhere else (a pin under its own condition), so each target framework's location is written.
        private sealed record Written(IReadOnlyList<(string Location, bool Changed)> Locations, string Value, bool PerTargetFramework = false) : Write
        {
            public Written(bool changed, string location, string value, bool perTargetFramework = false)
                : this([(location, changed)], value, perTargetFramework)
            {
            }
        }

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
                FileBasedDirectiveVersion => UpdateFileBasedDirectives(context, ReferenceKind.Sdk, upgrade.Version.ToString()),
                FileBasedPackageDirectiveVersion => UpdateFileBasedDirectives(context, ReferenceKind.Package, range.ToVersionString()),
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

                    case Written written:
                        foreach (var (location, changed) in written.Locations)
                        {
                            _reachedLocations?.Add(location);
                            if (changed)
                            {
                                // No project has been restored since this write, whatever was restored at the value before.
                                _writtenLocations[location] = new WrittenLocation(written.Value);
                                upgraded.Add(_writtenLocations[location]);
                            }
                            else if (WroteValue(location, written.Value))
                            {
                                upgraded.Add(_writtenLocations[location]);
                            }
                        }

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

            var projectFullPath = PathCase.OnDisk(_fileSystem, context.ProjectPath);
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

        // Writes a property-backed version in every file that holds it. Each file is its own location, which apps that
        // include it share.
        private Write UpdateFileBasedVariable(Context context, PackageVariableInfo variableInfo)
        {
            var version = context.Upgrade.Version;
            var locations = FileBasedVariableLocations(context.Directives, context.ProjectPath, variableInfo);
            var write = context.Directives.UpdateVariable(context.ProjectPath, variableInfo, version);
            if (write.Failure != null)
            {
                return new WriteFailed(write.Failure);
            }

            bool Changed(string file) => write.ChangedFiles.Contains(file, FileBasedAppSources.PathComparer);
            return write.ChangedFiles.Count > 0 || IsFileBasedAppVariableAtRequestedVersion(variableInfo, version)
                ? new Written([.. locations.Select(location => (location.Location, Changed(location.File)))], version.ToString())
                : new WriteFailed(
                    $"Failed to update file-based app '{context.ProjectPath}' for '{context.Name}'. " +
                    "Could not update the matching #:property value or #:package/#:sdk directive.");
        }

        // Writes the directives for the reference in every file of the app that declares it. Each file is its own
        // location, which apps that include it share.
        private Write UpdateFileBasedDirectives(Context context, ReferenceKind kind, string versionText)
        {
            var write = context.Directives.WriteDirective(context.ProjectPath, kind, context.Name, versionText);
            if (write.Failure != null)
            {
                return new WriteFailed(write.Failure);
            }

            return new Written(
                [.. write.DeclaringFiles.Select(file => (DirectiveLocation(file, kind, context.Name), write.ChangedFiles.Contains(file)))],
                versionText);
        }

        private string DirectiveLocation(string filePath, ReferenceKind kind, string name) =>
            Location(filePath, $"#:{kind} {name}");

        // The locations of a file-based app's property-backed version, with the file each is in: each file with the
        // property and each with a literal-version directive for the reference (which the upgrade writes too), or for
        // an SDK named through a property, each file with its directive.
        private IReadOnlyList<(string File, string Location)> FileBasedVariableLocations(FileBasedAppDirectives directives, string projectPath, PackageVariableInfo variableInfo)
        {
            var files = directives.VariableFiles(projectPath, variableInfo);
            if (string.IsNullOrEmpty(variableInfo.VariableName))
            {
                return [.. files.Select(file => (file, DirectiveLocation(file, variableInfo.ReferenceKind, variableInfo.PackageName)))];
            }

            return
            [
                .. files.Select(file => (file, Location(file, $"$({variableInfo.VariableName})"))),
                .. directives.LiteralDeclaringFiles(projectPath, variableInfo.ReferenceKind, variableInfo.PackageName)
                    .Select(file => (file, DirectiveLocation(file, variableInfo.ReferenceKind, variableInfo.PackageName))),
            ];
        }

        // `dotnet add package` cannot update an MSBuild project SDK, so rewrite its version in the project file.
        private Write UpdateProjectSdk(Context context, ProjectSdkReference sdk)
        {
            var version = context.Upgrade.Version;
            var location = Location(_fileSystem.Path.GetFullPath(context.ProjectPath), $"Sdk {context.Name}");
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
        private Write UpdateVariable(Context context, PackageVariableInfo variableInfo, VersionRange range)
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

        private string VariableLocation(PackageVariableInfo variableInfo) =>
            Location(variableInfo.FilePath, $"$({variableInfo.VariableName})");

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
                ? new Written(updated, Location(filePath, $"{context.Name}|{elementIndex}"), versionText, perTargetFramework: true)
                : new Written(updated, Location(filePath, context.Name), versionText);
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

            return new Written(updated, Location(centralPackagesPath, context.Name), versionText);
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
