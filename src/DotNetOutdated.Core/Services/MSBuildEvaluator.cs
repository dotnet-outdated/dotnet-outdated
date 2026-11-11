using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

// A static approximation of MSBuild evaluation, used to find which property definition a package version
// actually resolves to. Only files MSBuild would import are visited (the nearest Directory.Build.props and
// Directory.Packages.props, the project, the nearest Directory.Build.targets, and their resolvable
// <Import>s, in that order). Properties are assigned in document order with last-assignment-wins semantics;
// items are resolved afterwards against the final property values. Where a condition can't be evaluated
// statically, a property assigned under it is marked ambiguous so callers can refuse to guess.
internal sealed class MSBuildEvaluator(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem;

    internal sealed class PropertyDefinition
    {
        public required string Name { get; init; }

        /// <summary>The trimmed text inside the property element.</summary>
        public required string RawValue { get; init; }

        /// <summary>The value after expanding property references, or <see langword="null"/> when that isn't possible.</summary>
        public string? Value { get; init; }

        public required string FilePath { get; init; }

        /// <summary>Index of this element among same-named properties under any <c>PropertyGroup</c> in <see cref="FilePath"/>.</summary>
        public int ElementIndex { get; init; }

        /// <summary>Assigned under a condition that couldn't be evaluated, or derived from such a property.</summary>
        public bool Ambiguous { get; init; }

        /// <summary>When <see cref="RawValue"/> is exactly <c>$(Other)</c>, the name of that property.</summary>
        public string? LinkName { get; init; }

        /// <summary>The definition of <see cref="LinkName"/> in effect when this property was assigned, if any.</summary>
        public PropertyDefinition? Link { get; init; }
    }

    internal sealed class PackageItem
    {
        public required string PackageName { get; init; }
        public required string ElementType { get; init; }
        public required string FilePath { get; init; }
        public required string VersionExpression { get; init; }
        public bool IsVersionOverride { get; init; }
    }

    internal sealed class EvaluationState(string projectPath, string projectDirectory)
    {
        public string ProjectPath { get; } = projectPath;
        public string ProjectDirectory { get; } = projectDirectory;
        public Dictionary<string, PropertyDefinition> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ImportedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Last version-setting item per package, per item type.
        public Dictionary<string, PackageItem> PackageReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageItem> GlobalPackageReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageItem> PackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Items in document order, evaluated once all properties are known (MSBuild's item pass follows its property pass).
        public List<(XElement Item, XElement Group, string FilePath)> PendingItems { get; } = [];
    }

    /// <summary>
    /// The MSBuild version variable behind each package version of the project: its evaluated Directory.*.props,
    /// the project file and Directory.Build.targets. Packages whose version is a literal are left out.
    /// </summary>
    public Dictionary<string, PackageVariableInfo> PackageVariables(string projectFilePath) =>
        _fileSystem.File.Exists(projectFilePath)
            ? BuildPackageVariables(Evaluate(projectFilePath, includeProjectFile: true))
            : new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates the files MSBuild would import for <paramref name="projectFilePath"/>. Without
    /// <paramref name="includeProjectFile"/> only the Directory.* files are evaluated, as for a file-based app.
    /// </summary>
    public EvaluationState Evaluate(string projectFilePath, bool includeProjectFile)
    {
        var projectFullPath = _fileSystem.Path.GetFullPath(projectFilePath);
        var projectDirectory = _fileSystem.Path.GetDirectoryName(projectFullPath) ?? string.Empty;
        var state = new EvaluationState(projectFullPath, projectDirectory);

        EvaluateFileIfPresent(FindFileAbove(projectDirectory, "Directory.Build.props"), state);
        EvaluateFileIfPresent(FindFileAbove(projectDirectory, "Directory.Packages.props"), state);
        if (includeProjectFile)
        {
            EvaluateFileIfPresent(projectFullPath, state);
        }

        EvaluateFileIfPresent(FindFileAbove(projectDirectory, "Directory.Build.targets"), state);

        var groupConditions = new Dictionary<XElement, bool?>();
        foreach (var (item, group, filePath) in state.PendingItems)
        {
            if (!groupConditions.TryGetValue(group, out var groupCondition))
            {
                groupCondition = groupConditions[group] = EvaluateCondition(group, filePath, state, null);
            }

            if (groupCondition != false)
            {
                EvaluateItem(item, filePath, state);
            }
        }

        return state;
    }

    private string? FindFileAbove(string startDirectory, string fileName)
    {
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = _fileSystem.Path.Combine(directory, fileName);
            if (_fileSystem.File.Exists(candidate))
            {
                return _fileSystem.Path.GetFullPath(candidate);
            }

            directory = _fileSystem.Path.GetDirectoryName(directory);
        }

        return null;
    }

    private void EvaluateFileIfPresent(string? filePath, EvaluationState state, bool uncertain = false)
    {
        if (string.IsNullOrEmpty(filePath) || !state.ImportedFiles.Add(filePath))
        {
            return;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(_fileSystem.File.ReadAllText(filePath));
        }
        catch
        {
            // Files that can't be read or parsed are ignored, as before.
            return;
        }

        // Element indexes must count every same-named property under any PropertyGroup (including ones that
        // aren't evaluated, such as those inside targets), because that is how the file is later edited.
        var elementIndexes = new Dictionary<XElement, int>();
        foreach (var group in doc.Descendants()
                     .Where(IsPropertyElement)
                     .GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
        {
            var index = 0;
            foreach (var element in group)
            {
                elementIndexes[element] = index++;
            }
        }

        if (doc.Root != null)
        {
            EvaluateChildren(doc.Root, filePath, state, elementIndexes, uncertain);
        }
    }

    private void EvaluateChildren(
        XElement parent,
        string filePath,
        EvaluationState state,
        Dictionary<XElement, int> elementIndexes,
        bool uncertain)
    {
        foreach (var element in parent.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                {
                    // Properties assigned in the group count as empty when undefined, so the group-level default
                    // idiom <PropertyGroup Condition="'$(X)' == ''"><X>...</X></PropertyGroup> can be evaluated.
                    var assigned = element.Elements().Select(e => e.Name.LocalName).ToArray();
                    var groupCondition = EvaluateCondition(element, filePath, state, assigned);
                    if (groupCondition == false)
                    {
                        break;
                    }

                    foreach (var property in element.Elements())
                    {
                        EvaluateProperty(property, filePath, state, elementIndexes, uncertain || groupCondition == null);
                    }

                    break;
                }

                case "ItemGroup":
                {
                    foreach (var item in element.Elements())
                    {
                        state.PendingItems.Add((item, element, filePath));
                    }

                    break;
                }

                case "ImportGroup":
                {
                    var groupCondition = EvaluateCondition(element, filePath, state, null);
                    if (groupCondition != false)
                    {
                        EvaluateChildren(element, filePath, state, elementIndexes, uncertain || groupCondition == null);
                    }

                    break;
                }

                case "Import":
                    EvaluateImport(element, filePath, state, uncertain);
                    break;

                case "Choose":
                    EvaluateChoose(element, filePath, state, elementIndexes, uncertain);
                    break;

                // Targets, UsingTask, ProjectExtensions etc. don't contribute to evaluation.
            }
        }
    }

    private void EvaluateChoose(
        XElement choose,
        string filePath,
        EvaluationState state,
        Dictionary<XElement, int> elementIndexes,
        bool uncertain)
    {
        var anyUnknown = false;
        foreach (var branch in choose.Elements())
        {
            if (branch.Name.LocalName == "When")
            {
                var condition = EvaluateCondition(branch, filePath, state, null);
                if (condition == false)
                {
                    continue;
                }

                anyUnknown |= condition == null;
                EvaluateChildren(branch, filePath, state, elementIndexes, uncertain || anyUnknown);
                if (condition == true)
                {
                    return;
                }
            }
            else if (branch.Name.LocalName == "Otherwise")
            {
                EvaluateChildren(branch, filePath, state, elementIndexes, uncertain || anyUnknown);
                return;
            }
        }
    }

    private void EvaluateProperty(
        XElement property,
        string filePath,
        EvaluationState state,
        Dictionary<XElement, int> elementIndexes,
        bool uncertain)
    {
        var name = property.Name.LocalName;
        var condition = EvaluateCondition(property, filePath, state, [name]);
        if (condition == false)
        {
            return;
        }

        var rawValue = property.Value.Trim();
        var ambiguous = uncertain || condition == null;
        string? linkName = null;
        PropertyDefinition? link = null;
        string? value;

        if (VersionExpression.TryGetSinglePropertyReference(rawValue, out var referenced) && !IsWellKnownProperty(referenced))
        {
            linkName = referenced;
            state.Properties.TryGetValue(referenced, out link);
            ambiguous |= link?.Ambiguous == true;
            value = link?.Value;
        }
        else
        {
            value = Expand(rawValue, filePath, state, null);
        }

        state.Properties[name] = new PropertyDefinition
        {
            Name = name,
            RawValue = rawValue,
            Value = value,
            FilePath = filePath,
            ElementIndex = elementIndexes.TryGetValue(property, out var index) ? index : -1,
            Ambiguous = ambiguous,
            LinkName = linkName,
            Link = link,
        };
    }

    private void EvaluateItem(XElement item, string filePath, EvaluationState state)
    {
        var slot = item.Name.LocalName switch
        {
            "PackageReference" => state.PackageReferences,
            "GlobalPackageReference" => state.GlobalPackageReferences,
            "PackageVersion" => state.PackageVersions,
            _ => null,
        };

        if (slot == null)
        {
            return;
        }

        var remove = GetAttributeValue(item, "Remove");
        if (remove != null)
        {
            // A removal whose condition can't be evaluated is ignored rather than guessed.
            if (EvaluateCondition(item, filePath, state, null) == true)
            {
                foreach (var name in remove.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    slot.Remove(name);
                }
            }

            return;
        }

        var packageName = (GetAttributeValue(item, "Include") ?? GetAttributeValue(item, "Update"))?.Trim();
        if (string.IsNullOrEmpty(packageName) ||
            !TryGetPackageVersionExpression(item, out var versionExpression, out var isVersionOverride))
        {
            return;
        }

        // Items whose condition can't be evaluated are treated as applying (the previous behaviour).
        if (EvaluateCondition(item, filePath, state, null) == false)
        {
            return;
        }

        // A later Include or Update that sets a version (literal or not) supersedes earlier ones.
        slot[packageName] = new PackageItem
        {
            PackageName = packageName,
            ElementType = item.Name.LocalName,
            FilePath = filePath,
            VersionExpression = versionExpression.Trim(),
            IsVersionOverride = isVersionOverride,
        };
    }

    private void EvaluateImport(XElement import, string filePath, EvaluationState state, bool uncertain)
    {
        if (GetAttributeValue(import, "Sdk") != null)
        {
            return;
        }

        var condition = EvaluateCondition(import, filePath, state, null);
        if (condition == false)
        {
            return;
        }

        var rawPath = GetAttributeValue(import, "Project");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        var expanded = Expand(rawPath.Trim(), filePath, state, null);
        if (string.IsNullOrEmpty(expanded) || expanded.IndexOfAny(['*', '?', ';']) >= 0)
        {
            return;
        }

        var separator = _fileSystem.Path.DirectorySeparatorChar;
        var normalized = expanded.Replace('\\', separator).Replace('/', separator);
        var currentDirectory = _fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
        var combined = _fileSystem.Path.IsPathRooted(normalized) ? normalized : _fileSystem.Path.Combine(currentDirectory, normalized);
        var resolved = _fileSystem.Path.GetFullPath(combined);
        if (!_fileSystem.File.Exists(resolved))
        {
            return;
        }

        // An import under an unknown condition is still followed, but what it assigns is ambiguous.
        EvaluateFileIfPresent(resolved, state, uncertain || condition == null);
    }

    private bool? EvaluateCondition(XElement element, string filePath, EvaluationState state, IReadOnlyCollection<string>? assignedProperties)
    {
        var condition = GetAttributeValue(element, "Condition");
        return condition == null
            ? true
            : MSBuildCondition.Evaluate(
                condition,
                text => Expand(text, filePath, state, assignedProperties),
                path => Exists(path, state));
    }

    private bool? Exists(string path, EvaluationState state)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var separator = _fileSystem.Path.DirectorySeparatorChar;
        var normalized = path.Trim().Replace('\\', separator).Replace('/', separator);
        var combined = _fileSystem.Path.IsPathRooted(normalized)
            ? normalized
            : _fileSystem.Path.Combine(state.ProjectDirectory, normalized);
        var fullPath = _fileSystem.Path.GetFullPath(combined);
        return _fileSystem.File.Exists(fullPath) || _fileSystem.Directory.Exists(fullPath);
    }

    private static readonly string[] WellKnownProperties =
    [
        "MSBuildThisFileDirectory", "MSBuildThisFile", "MSBuildThisFileName", "MSBuildThisFileFullPath",
        "MSBuildProjectDirectory", "MSBuildProjectFullPath", "MSBuildProjectFile", "MSBuildProjectName",
    ];

    private static bool IsWellKnownProperty(string name) =>
        WellKnownProperties.Contains(name, StringComparer.OrdinalIgnoreCase);

    // $([MSBuild]::GetPathOfFileAbove('file', 'startDir')) is the usual way a Directory.Build.props imports its parent.
    private static readonly Regex GetPathOfFileAboveRegex = new(
        @"\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<file>[^'$()]+)'\s*(?:,\s*'(?<dir>[^']*)'\s*)?\)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Expands <c>$(Name)</c> references. Returns <see langword="null"/> when any reference is unknown, ambiguous,
    /// a property function (other than <c>GetPathOfFileAbove</c>), or an item/metadata reference.
    /// An undefined property in <paramref name="assignedProperties"/> (those the condition's element assigns)
    /// expands to empty, so the common <c>Condition="'$(X)' == ''"</c> default pattern can be evaluated.
    /// </summary>
    private string? Expand(string text, string filePath, EvaluationState state, IReadOnlyCollection<string>? assignedProperties)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var failed = false;
        var currentDirectory = _fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
        string WithTrailingSlash(string dir) =>
            string.IsNullOrEmpty(dir) ? string.Empty : dir.TrimEnd('\\', '/') + _fileSystem.Path.DirectorySeparatorChar;

        string ExpandSimple(string input) => VersionExpression.PropertyReferenceRegex().Replace(input, match =>
        {
            var name = match.Groups[1].Value;
            switch (name.ToUpperInvariant())
            {
                case "MSBUILDTHISFILEDIRECTORY": return WithTrailingSlash(currentDirectory);
                case "MSBUILDTHISFILE": return _fileSystem.Path.GetFileName(filePath);
                case "MSBUILDTHISFILENAME": return _fileSystem.Path.GetFileNameWithoutExtension(filePath);
                case "MSBUILDTHISFILEFULLPATH": return filePath;
                case "MSBUILDPROJECTDIRECTORY": return state.ProjectDirectory;
                case "MSBUILDPROJECTFULLPATH": return state.ProjectPath;
                case "MSBUILDPROJECTFILE": return _fileSystem.Path.GetFileName(state.ProjectPath);
                case "MSBUILDPROJECTNAME": return _fileSystem.Path.GetFileNameWithoutExtension(state.ProjectPath);
            }

            if (state.Properties.TryGetValue(name, out var definition))
            {
                if (definition.Ambiguous || definition.Value == null)
                {
                    failed = true;
                    return match.Value;
                }

                return definition.Value;
            }

            if (assignedProperties != null && assignedProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            failed = true;
            return match.Value;
        });

        var expanded = GetPathOfFileAboveRegex.Replace(text, match =>
        {
            var start = match.Groups["dir"].Success ? ExpandSimple(match.Groups["dir"].Value) : currentDirectory;
            if (failed)
            {
                return match.Value;
            }

            start = _fileSystem.Path.IsPathRooted(start) ? start : _fileSystem.Path.Combine(currentDirectory, start);
            return FindFileAbove(_fileSystem.Path.GetFullPath(start), match.Groups["file"].Value) ?? string.Empty;
        });

        expanded = ExpandSimple(expanded);
        if (failed ||
            expanded.Contains("$(", StringComparison.Ordinal) ||
            expanded.Contains("@(", StringComparison.Ordinal) ||
            expanded.Contains("%(", StringComparison.Ordinal))
        {
            return null;
        }

        return expanded;
    }

    internal static bool IsPropertyElement(XElement element) =>
        element.Parent != null &&
        element.Parent.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, PackageVariableInfo> BuildPackageVariables(EvaluationState state)
    {
        var result = new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);
        var packageNames = state.PackageReferences.Keys
            .Concat(state.GlobalPackageReferences.Keys)
            .Concat(state.PackageVersions.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var packageName in packageNames)
        {
            // A version on the PackageReference (Version or VersionOverride) takes precedence over central versions.
            var item = state.PackageReferences.GetValueOrDefault(packageName) ??
                       state.GlobalPackageReferences.GetValueOrDefault(packageName) ??
                       state.PackageVersions[packageName];

            var info = CreatePackageVariableInfo(item, state);
            if (info != null)
            {
                result[packageName] = info;
            }
        }

        return result;
    }

    private static PackageVariableInfo? CreatePackageVariableInfo(PackageItem item, EvaluationState state)
    {
        if (!item.VersionExpression.Contains("$(", StringComparison.Ordinal))
        {
            return null; // Literal version: nothing to preserve.
        }

        var info = new PackageVariableInfo
        {
            PackageName = item.PackageName,
            PackageReferenceFilePath = item.FilePath,
            ElementType = item.ElementType,
            PackageReferenceVersion = item.VersionExpression,
            IsVersionOverride = item.IsVersionOverride,
            VariableValue = string.Empty,
        };

        if (!VersionExpression.TryParse(item.VersionExpression, out var variableName, out _))
        {
            info.VariableName = VersionExpression.PropertyReferenceRegex().Match(item.VersionExpression) is { Success: true } m ? m.Groups[1].Value : string.Empty;
            info.FilePath = item.FilePath;
            info.UnsupportedReason =
                $"its version '{item.VersionExpression}' is neither a single property reference nor a range whose lower bound is a property reference";
            return info;
        }

        // Follow $(A) -> <A>$(B)</A> -> <B>1.0.0</B> to the property that holds the literal version.
        state.Properties.TryGetValue(variableName, out var definition);
        PropertyDefinition? anchor = null;
        string rootName = variableName;
        var ambiguous = false;
        while (definition != null)
        {
            ambiguous |= definition.Ambiguous;
            rootName = definition.Name;
            if (definition.LinkName == null)
            {
                break;
            }

            if (definition.Link == null)
            {
                // The chain ends at a property defined outside the evaluated files (e.g. by an SDK):
                // override it just before the alias that reads it.
                anchor = definition;
                rootName = definition.LinkName;
            }

            definition = definition.Link;
        }

        info.VariableName = rootName;

        if (ambiguous)
        {
            info.FilePath = (definition ?? anchor)?.FilePath ?? item.FilePath;
            info.UnsupportedReason =
                $"the value of $({rootName}) depends on a condition that can't be evaluated statically";
            return info;
        }

        if (definition != null)
        {
            info.FilePath = definition.FilePath;
            info.VariableValue = definition.Value ?? definition.RawValue;
            info.PropertyElementIndex = definition.ElementIndex;
            if (definition.RawValue.Contains("$(", StringComparison.Ordinal) ||
                definition.RawValue.Contains("@(", StringComparison.Ordinal) ||
                definition.RawValue.Contains("%(", StringComparison.Ordinal))
            {
                info.UnsupportedReason = $"$({rootName}) is defined by the expression '{definition.RawValue}' rather than a version";
            }
        }
        else if (anchor != null)
        {
            info.FilePath = anchor.FilePath;
            info.InsertBeforeProperty = (anchor.Name, anchor.ElementIndex);
        }
        else
        {
            // Not defined in any evaluated file (e.g. $(MauiVersion) from a workload): define it next to the reference.
            info.FilePath = item.FilePath;
            info.InsertProperty = true;
        }

        return info;
    }

    private static bool TryGetPackageVersionExpression(XElement element, out string versionValue, out bool isVersionOverride)
    {
        var overrideValue = GetAttributeOrElementValue(element, "VersionOverride");
        if (!string.IsNullOrEmpty(overrideValue))
        {
            versionValue = overrideValue;
            isVersionOverride = true;
            return true;
        }

        versionValue = GetAttributeOrElementValue(element, "Version") ?? string.Empty;
        isVersionOverride = false;
        return !string.IsNullOrEmpty(versionValue);
    }

    private static string? GetAttributeOrElementValue(XElement element, string name) =>
        GetAttributeValue(element, name) ??
        element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static string? GetAttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
