using System;
using System.Collections.Generic;
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
public sealed partial class VariableTrackingService
{
    internal sealed class PropertyDefinition
    {
        public string Name { get; init; }

        /// <summary>The trimmed text inside the property element.</summary>
        public string RawValue { get; init; }

        /// <summary>The value after expanding property references, or <see langword="null"/> when that isn't possible.</summary>
        public string Value { get; init; }

        public string FilePath { get; init; }

        /// <summary>Index of this element among same-named properties under any <c>PropertyGroup</c> in <see cref="FilePath"/>.</summary>
        public int ElementIndex { get; init; }

        /// <summary>Assigned under a condition that couldn't be evaluated, or derived from such a property.</summary>
        public bool Ambiguous { get; init; }

        /// <summary>When <see cref="RawValue"/> is exactly <c>$(Other)</c>, the name of that property.</summary>
        public string LinkName { get; init; }

        /// <summary>The definition of <see cref="LinkName"/> in effect when this property was assigned, if any.</summary>
        public PropertyDefinition Link { get; init; }
    }

    private sealed class PackageItem
    {
        public string PackageName { get; init; }
        public string ElementType { get; init; }
        public string FilePath { get; init; }
        public string VersionExpression { get; init; }
        public bool IsVersionOverride { get; init; }
    }

    private sealed class EvaluationState(string projectPath, string projectDirectory)
    {
        public string ProjectPath { get; } = projectPath;
        public string ProjectDirectory { get; } = projectDirectory;
        public Dictionary<string, PropertyDefinition> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ImportedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Last version-setting item per package, per item type.
        public Dictionary<string, PackageItem> PackageReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageItem> GlobalPackageReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageItem> PackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private EvaluationState EvaluateProject(string projectFilePath, bool includeProjectFile)
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
        return state;
    }

    private string FindFileAbove(string startDirectory, string fileName)
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

    private void EvaluateFileIfPresent(string filePath, EvaluationState state, bool uncertain = false)
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
                    var groupCondition = EvaluateCondition(element, filePath, state, null);
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
                    if (EvaluateCondition(element, filePath, state, null) == false)
                    {
                        break;
                    }

                    foreach (var item in element.Elements())
                    {
                        EvaluateItem(item, filePath, state);
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
        var condition = EvaluateCondition(property, filePath, state, name);
        if (condition == false)
        {
            return;
        }

        var rawValue = property.Value.Trim();
        var ambiguous = uncertain || condition == null;
        string linkName = null;
        PropertyDefinition link = null;
        string value;

        if (TryGetSinglePropertyReference(rawValue, out var referenced) && !IsWellKnownProperty(referenced))
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
        if (GetAttributeValue(import, "Sdk") != null ||
            EvaluateCondition(import, filePath, state, null) == false)
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
        EvaluateFileIfPresent(resolved, state, uncertain);
    }

    private bool? EvaluateCondition(XElement element, string filePath, EvaluationState state, string assignedProperty)
    {
        var condition = GetAttributeValue(element, "Condition");
        return condition == null
            ? true
            : MSBuildCondition.Evaluate(
                condition,
                text => Expand(text, filePath, state, assignedProperty),
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
    /// An undefined <paramref name="assignedProperty"/> expands to empty, so the common
    /// <c>Condition="'$(X)' == ''"</c> default pattern can be evaluated.
    /// </summary>
    private string Expand(string text, string filePath, EvaluationState state, string assignedProperty)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var failed = false;
        var currentDirectory = _fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
        string WithTrailingSlash(string dir) =>
            string.IsNullOrEmpty(dir) ? string.Empty : dir.TrimEnd('\\', '/') + _fileSystem.Path.DirectorySeparatorChar;

        string ExpandSimple(string input) => MsBuildPropertyReferenceRegex().Replace(input, match =>
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

            if (assignedProperty != null && name.Equals(assignedProperty, StringComparison.OrdinalIgnoreCase))
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

    private static bool IsPropertyElement(XElement element) =>
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
                       state.PackageVersions.GetValueOrDefault(packageName);

            var info = CreatePackageVariableInfo(item, state);
            if (info != null)
            {
                result[packageName] = info;
            }
        }

        return result;
    }

    private static PackageVariableInfo CreatePackageVariableInfo(PackageItem item, EvaluationState state)
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

        if (!TryParseVersionExpression(item.VersionExpression, out var variableName, out _))
        {
            info.VariableName = MsBuildPropertyReferenceRegex().Match(item.VersionExpression) is { Success: true } m ? m.Groups[1].Value : string.Empty;
            info.FilePath = item.FilePath;
            info.UnsupportedReason =
                $"its version '{item.VersionExpression}' is neither a single property reference nor a range whose lower bound is a property reference";
            return info;
        }

        // Follow $(A) -> <A>$(B)</A> -> <B>1.0.0</B> to the property that holds the literal version.
        state.Properties.TryGetValue(variableName, out var definition);
        PropertyDefinition anchor = null;
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

    // Matches "[ $(Name) , upper ]" style ranges whose lower bound is exactly one property reference.
    private static readonly Regex LowerBoundRangeRegex = new(
        @"^\s*(?<open>[\[(])\s*\$\(\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\)\s*,(?<upper>[^,$\[\]()]*)(?<close>[\])])\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the version expressions whose variable can be updated without changing what the expression means:
    /// a single property reference (<c>$(V)</c>) or a range whose lower bound is a property (<c>[$(V), 2.0.0)</c>).
    /// </summary>
    internal static bool TryParseVersionExpression(string expression, out string variableName, out bool isRange)
    {
        isRange = false;
        if (TryGetSinglePropertyReference(expression, out variableName))
        {
            return true;
        }

        var match = LowerBoundRangeRegex.Match(expression ?? string.Empty);
        if (!match.Success)
        {
            variableName = string.Empty;
            return false;
        }

        variableName = match.Groups["name"].Value;
        isRange = true;
        return true;
    }

    /// <summary>
    /// Evaluates a supported version expression for a given value of its variable.
    /// </summary>
    internal static bool TryEvaluateVersionExpression(string expression, string variableValue, out VersionRange range)
    {
        range = null;
        if (!TryParseVersionExpression(expression, out var variableName, out var isRange))
        {
            return false;
        }

        if (!isRange)
        {
            return VersionRange.TryParse(variableValue ?? string.Empty, out range);
        }

        var substituted = MsBuildPropertyReferenceRegex().Replace(expression, variableValue ?? string.Empty);
        return VersionRange.TryParse(substituted, out range);
    }

    /// <summary>
    /// The expression to write back for <paramref name="newRange"/>: the original when it already evaluates to
    /// the requested range with the variable at its new value, otherwise the requested range with the variable
    /// as its lower bound (so a dropped or changed upper bound is never resurrected).
    /// </summary>
    internal static string ComputeVersionExpression(string originalExpression, string variableName, VersionRange newRange)
    {
        if (!TryParseVersionExpression(originalExpression, out _, out var isRange) || !isRange)
        {
            return originalExpression;
        }

        if (TryEvaluateVersionExpression(originalExpression, newRange.MinVersion.ToString(), out var evaluated) &&
            evaluated.Equals(newRange))
        {
            return originalExpression;
        }

        var reference = $"$({variableName})";
        if (newRange.IsPlainVersion())
        {
            return reference;
        }

        var upper = newRange.HasUpperBound ? newRange.MaxVersion.ToNormalizedString() : string.Empty;
        return $"{(newRange.IsMinInclusive ? "[" : "(")}{reference}, {upper}{(newRange.IsMaxInclusive ? "]" : ")")}";
    }
}
