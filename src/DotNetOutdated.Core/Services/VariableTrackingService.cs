using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotNetOutdated.Core;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

public interface IVariableTrackingService
{
    /// <summary>
    /// Scans a project file and its imports (like Directory.Packages.props, Directory.Build.props) for variable-based package versions
    /// </summary>
    Dictionary<string, PackageVariableInfo> DiscoverPackageVariables(string projectFilePath);

    /// <summary>
    /// Updates a package's variable value and restores the variable reference after dotnet add package overwrites it
    /// </summary>
    /// <param name="variableInfo">Information about the package variable to update</param>
    /// <param name="newVersion">The new version to set</param>
    void UpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion);

    /// <summary>
    /// Attempts to update a package's variable value and restore the variable reference after dotnet add package overwrites it.
    /// </summary>
    /// <param name="variableInfo">Information about the package variable to update</param>
    /// <param name="newVersion">The new version to set</param>
    /// <returns><see langword="true"/> when at least one file was updated; otherwise <see langword="false"/>.</returns>
    bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion);

    /// <summary>
    /// Updates a package's variable value to a version range (e.g. <c>[13.0.4, 14.0.0)</c>) and restores the variable reference.
    /// File-based app variables only hold single versions, so they receive the range's lower bound.
    /// </summary>
    /// <remarks>
    /// The default implementation keeps existing implementers of this interface working: a plain minimum
    /// version is passed to the <see cref="NuGetVersion"/> overload, and any other range is not updated.
    /// </remarks>
    void UpdatePackageVariable(PackageVariableInfo variableInfo, VersionRange newVersionRange) =>
        TryUpdatePackageVariable(variableInfo, newVersionRange);

    /// <inheritdoc cref="UpdatePackageVariable(PackageVariableInfo, VersionRange)"/>
    /// <returns><see langword="true"/> when at least one file was updated; otherwise <see langword="false"/>.</returns>
    bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, VersionRange newVersionRange)
    {
        ArgumentNullException.ThrowIfNull(newVersionRange);

        return newVersionRange.IsPlainVersion() && TryUpdatePackageVariable(variableInfo, newVersionRange.MinVersion);
    }

    /// <summary>
    /// Updates a literal <c>#:package</c> or <c>#:sdk</c> directive in a file-based app source file.
    /// </summary>
    /// <returns><see langword="true"/> when the directive line was updated; otherwise <see langword="false"/>.</returns>
    bool UpdateFileBasedAppDirectReference(string projectFilePath, string name, FileBasedAppReferenceKind kind, NuGetVersion newVersion);

    /// <summary>
    /// Discovers <c>#:package</c> and <c>#:sdk</c> references declared in a file-based app, including literal and variable-backed versions.
    /// </summary>
    IReadOnlyList<FileBasedAppReference> DiscoverFileBasedAppReferences(string projectFilePath);

    /// <summary>
    /// Clears the internal cache. Useful for testing or when you know files have changed.
    /// </summary>
    void ClearCache();
}

// Scans project files and their .props imports for MSBuild variable-based package versions,
// and restores those variable references after dotnet add package overwrites them with literals.
// Uses a hybrid approach: XDocument to locate values, regex to update files while preserving formatting.
public sealed partial class VariableTrackingService : IVariableTrackingService
{
    private readonly IFileSystem _fileSystem;
    private readonly Action<string> _onWarning;
    // Project analysis populates these shared caches in parallel.
    private readonly ConcurrentDictionary<string, Dictionary<string, PackageVariableInfo>> _cache;
    private readonly ConcurrentDictionary<string, FileBasedAppScanResult> _fileBasedAppScanCache;

    public VariableTrackingService(IFileSystem fileSystem, Action<string> onWarning = null)
    {
        _fileSystem = fileSystem;
        _onWarning = onWarning;
        _cache = new ConcurrentDictionary<string, Dictionary<string, PackageVariableInfo>>(StringComparer.OrdinalIgnoreCase);
        _fileBasedAppScanCache = new ConcurrentDictionary<string, FileBasedAppScanResult>(StringComparer.OrdinalIgnoreCase);
    }

    public void ClearCache()
    {
        _cache.Clear();
        _fileBasedAppScanCache.Clear();
    }

    public void UpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion) =>
        TryUpdatePackageVariable(variableInfo, newVersion);

    public bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        ArgumentNullException.ThrowIfNull(newVersion);
        return TryUpdatePackageVariable(variableInfo, newVersion, new VersionRange(newVersion));
    }

    public void UpdatePackageVariable(PackageVariableInfo variableInfo, VersionRange newVersionRange) =>
        TryUpdatePackageVariable(variableInfo, newVersionRange);

    public bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, VersionRange newVersionRange)
    {
        ArgumentNullException.ThrowIfNull(newVersionRange);
        if (!newVersionRange.HasLowerBound)
        {
            throw new ArgumentException($"Version range '{newVersionRange}' must have a lower bound.", nameof(newVersionRange));
        }

        return TryUpdatePackageVariable(variableInfo, newVersionRange.MinVersion, newVersionRange);
    }

    internal static bool TryGetSinglePropertyReference(string expression, out string propertyName)
    {
        propertyName = string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var trimmed = expression.Trim();
        var match = MsBuildPropertyReferenceRegex().Match(trimmed);
        if (!match.Success || match.Index != 0 || match.Length != trimmed.Length)
        {
            return false;
        }

        propertyName = match.Groups[1].Value;
        return true;
    }

    // newVersion is used for file-based app variables and range-wrapped variable references; newRange is what MSBuild references should evaluate to.
    private bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion, VersionRange newRange)
    {
        try
        {
            if (variableInfo.ElementType is PackageVariableInfo.FileBasedPackageDirectiveElementType
                                         or PackageVariableInfo.FileBasedSdkDirectiveElementType)
            {
                var directiveRegex = variableInfo.ElementType == PackageVariableInfo.FileBasedSdkDirectiveElementType
                    ? SdkDirectiveReplaceRegex()
                    : PackageDirectiveReplaceRegex();
                var updated = UpdateFileBasedAppVariable(variableInfo, newVersion, directiveRegex);
                if (updated)
                {
                    InvalidateCache(variableInfo.FilePath, variableInfo.PackageReferenceFilePath);
                }

                return updated;
            }

            if (variableInfo.UnsupportedReason != null)
            {
                _onWarning?.Invoke(
                    $"Not preserving the version variable of '{variableInfo.PackageName}': {variableInfo.UnsupportedReason}.");
                return false;
            }

            // $(V) holds the whole requested range; in [$(V), upper) it holds only the lower bound.
            var isRangeExpression = !string.IsNullOrWhiteSpace(variableInfo.PackageReferenceVersion) &&
                                    TryParseVersionExpression(variableInfo.PackageReferenceVersion, out _, out var isRange) &&
                                    isRange;
            var propertyValueToWrite = isRangeExpression ? newVersion.ToString() : newRange.ToVersionString();

            // Step 1: Update the property value in the file where it's defined (or insert it if implicit/SDK-defined)
            string propertyFilePath = variableInfo.FilePath;
            var propertyWrite = TryWriteProperty(variableInfo, propertyValueToWrite);
            if (propertyWrite == PropertyWrite.Failed)
            {
                // Restoring the reference now would make the package resolve to the property's old value.
                return false;
            }

            var msbuildUpdated = propertyWrite == PropertyWrite.Written;

            // Step 2: Restore the variable reference in the PackageReference file (might be different from property file)
            string packageRefFilePath = variableInfo.PackageReferenceFilePath;
            if (_fileSystem.File.Exists(packageRefFilePath))
            {
                var packageRefFile = EncodedText.Read(_fileSystem, packageRefFilePath);
                string packageRefContent = packageRefFile.Content;
                _ = XDocument.Parse(packageRefContent);

                var versionExpression = string.IsNullOrEmpty(variableInfo.PackageReferenceVersion)
                    ? $"$({variableInfo.VariableName})"
                    : isRangeExpression
                        ? ComputeVersionExpression(variableInfo.PackageReferenceVersion, ReferencedVariableName(variableInfo), newRange)
                        : variableInfo.PackageReferenceVersion;

                var newPackageRefContent = PackageVersionMarkup.ReplacePackageVersion(
                    packageRefContent,
                    [variableInfo.ElementType],
                    variableInfo.PackageName,
                    versionExpression,
                    variableInfo.PackageReferenceVersion,
                    newRange);

                if (!string.Equals(packageRefContent, newPackageRefContent, StringComparison.Ordinal))
                {
                    packageRefFile.Write(_fileSystem, packageRefFilePath, newPackageRefContent);
                    msbuildUpdated = true;
                }
            }

            if (msbuildUpdated)
            {
                InvalidateCache(propertyFilePath, packageRefFilePath);
            }

            return msbuildUpdated;
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke(
                $"Failed to update package reference '{variableInfo.PackageName}' ({variableInfo.ElementType}): {ex.Message}");
            return false;
        }
    }

    private static string ReferencedVariableName(PackageVariableInfo variableInfo) =>
        TryParseVersionExpression(variableInfo.PackageReferenceVersion, out var name, out _) ? name : variableInfo.VariableName;

    private enum PropertyWrite { Written, AlreadyCurrent, Failed }

    private PropertyWrite TryWriteProperty(PackageVariableInfo variableInfo, string newValue)
    {
        if (string.IsNullOrEmpty(variableInfo.FilePath) ||
            string.IsNullOrEmpty(variableInfo.VariableName) ||
            !_fileSystem.File.Exists(variableInfo.FilePath))
        {
            return PropertyWrite.Failed;
        }

        var propertyFile = EncodedText.Read(_fileSystem, variableInfo.FilePath);
        var content = propertyFile.Content;
        var doc = XDocument.Parse(content);
        string newContent;

        if (variableInfo.InsertBeforeProperty is { } anchor)
        {
            newContent = PackageVersionMarkup.InsertPropertyBefore(content, anchor.Name, anchor.Index, variableInfo.VariableName, newValue);
        }
        else if (variableInfo.InsertProperty || variableInfo.PropertyElementIndex < 0)
        {
            var elements = PropertyElements(doc, variableInfo.VariableName);
            var topLevel = elements.FindLastIndex(e => IsUnconditionalTopLevel(e.Parent) && e.Attribute("Condition") == null);
            newContent = topLevel >= 0
                // Already defined (for example by an earlier upgrade that inserted it): update that definition.
                ? PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, topLevel, newValue)
                : !variableInfo.InsertProperty && elements.Count > 0
                    // Legacy callers that don't say which definition: the last one.
                    ? PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, elements.Count - 1, newValue)
                    : PackageVersionMarkup.InsertProperty(content, InsertionGroupIndex(doc), variableInfo.VariableName, newValue);
        }
        else
        {
            var elements = PropertyElements(doc, variableInfo.VariableName);
            var index = variableInfo.PropertyElementIndex;
            if (index < elements.Count && string.Equals(elements[index].Value.Trim(), newValue, StringComparison.Ordinal))
            {
                return PropertyWrite.AlreadyCurrent;
            }

            if (index >= elements.Count ||
                (!string.IsNullOrEmpty(variableInfo.VariableValue) &&
                 !string.Equals(elements[index].Value.Trim(), variableInfo.VariableValue, StringComparison.Ordinal)))
            {
                _onWarning?.Invoke(
                    $"Not updating $({variableInfo.VariableName}) in '{variableInfo.FilePath}': the file changed since it was scanned.");
                return PropertyWrite.Failed;
            }

            newContent = PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, index, newValue);
        }

        if (string.Equals(content, newContent, StringComparison.Ordinal))
        {
            return PropertyWrite.AlreadyCurrent;
        }

        if (!IsExpectedPropertyEdit(doc, newContent, variableInfo.VariableName, newValue))
        {
            _onWarning?.Invoke(
                $"Not updating $({variableInfo.VariableName}) in '{variableInfo.FilePath}': the edit did not change exactly one definition as expected.");
            return PropertyWrite.Failed;
        }

        propertyFile.Write(_fileSystem, variableInfo.FilePath, newContent);
        return PropertyWrite.Written;
    }

    // Guards the text-level edit against regex/XML disagreements: the edited document must still parse, and
    // exactly one definition of the property must differ (holding newValue) or have been added; nothing else
    // about the property's definitions may change.
    private static bool IsExpectedPropertyEdit(XDocument before, string newContent, string propertyName, string newValue)
    {
        XDocument after;
        try
        {
            after = XDocument.Parse(newContent);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        var oldValues = PropertyElements(before, propertyName).Select(e => e.Value.Trim()).ToList();
        var newValues = PropertyElements(after, propertyName).Select(e => e.Value.Trim()).ToList();

        if (newValues.Count == oldValues.Count)
        {
            var changed = Enumerable.Range(0, oldValues.Count).Where(i => oldValues[i] != newValues[i]).ToList();
            return changed.Count == 1 && newValues[changed[0]] == newValue;
        }

        if (newValues.Count != oldValues.Count + 1)
        {
            return false;
        }

        // Inserted: removing one occurrence of newValue must give back the original sequence.
        for (var i = 0; i < newValues.Count; i++)
        {
            if (newValues[i] == newValue && newValues.Where((_, j) => j != i).SequenceEqual(oldValues))
            {
                return true;
            }
        }

        return false;
    }

    // Updates the definition of variableName that is in effect for the file-based app at projectFilePath.
    private bool TryUpdateXmlPropertyFile(string projectFilePath, string propertyFilePath, string variableName, string newMSBuildValue)
    {
        if (string.IsNullOrEmpty(propertyFilePath) ||
            string.IsNullOrEmpty(variableName) ||
            !_fileSystem.File.Exists(propertyFilePath))
        {
            return false;
        }

        var properties = EvaluateProject(projectFilePath, includeProjectFile: false).Properties;
        if (!properties.TryGetValue(variableName, out var definition) ||
            definition.Ambiguous ||
            definition.ElementIndex < 0 ||
            !string.Equals(definition.FilePath, _fileSystem.Path.GetFullPath(propertyFilePath), StringComparison.OrdinalIgnoreCase))
        {
            _onWarning?.Invoke(
                $"Not updating $({variableName}) in '{propertyFilePath}': the definition in effect could not be determined.");
            return false;
        }

        var propertyFile = EncodedText.Read(_fileSystem, propertyFilePath);
        string propertyContent = propertyFile.Content;
        var newPropertyContent = PackageVersionMarkup.ReplacePropertyValueAt(propertyContent, variableName, definition.ElementIndex, newMSBuildValue);
        if (string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
        {
            return false;
        }

        propertyFile.Write(_fileSystem, propertyFilePath, newPropertyContent);
        return true;
    }

    private static List<XElement> PropertyElements(XDocument doc, string propertyName) =>
        doc.Descendants()
            .Where(e => IsPropertyElement(e) && e.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static bool IsUnconditionalTopLevel(XElement propertyGroup) =>
        propertyGroup.Parent != null &&
        propertyGroup.Parent == propertyGroup.Document?.Root &&
        propertyGroup.Attribute("Condition") == null;

    // The first unconditional PropertyGroup directly under <Project> (not in a target, Choose or conditional
    // group), as an index among the non-empty PropertyGroups that PackageVersionMarkup counts; -1 for a new group.
    private static int InsertionGroupIndex(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase) && !e.IsEmpty)
            .ToList()
            .FindIndex(IsUnconditionalTopLevel);

    public bool UpdateFileBasedAppDirectReference(string projectFilePath, string name, FileBasedAppReferenceKind kind, NuGetVersion newVersion)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return false;
        }

        var directiveName = kind == FileBasedAppReferenceKind.Sdk ? "sdk" : "package";
        var content = _fileSystem.File.ReadAllText(projectFilePath);
        var updatedContent = Regex.Replace(
            content,
            $@"(^[ \t]*#:[ \t]*{directiveName}[ \t]+{Regex.Escape(name)}@)([^\s\r\n]+)(.*?)(\r?\n|$)",
            match => $"{match.Groups[1].Value}{newVersion}{match.Groups[3].Value}{match.Groups[4].Value}",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (string.Equals(content, updatedContent, StringComparison.Ordinal))
        {
            return false;
        }

        _fileSystem.File.WriteAllText(projectFilePath, updatedContent);
        InvalidateCache(projectFilePath, projectFilePath);
        return true;
    }

    public IReadOnlyList<FileBasedAppReference> DiscoverFileBasedAppReferences(string projectFilePath)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        var scan = GetOrScanFileBasedApp(projectFilePath);
        var references = new List<FileBasedAppReference>(scan.LiteralReferences);

        foreach (var variableInfo in scan.PackageVariables.Values)
        {
            if (variableInfo.ElementType != PackageVariableInfo.FileBasedPackageDirectiveElementType &&
                variableInfo.ElementType != PackageVariableInfo.FileBasedSdkDirectiveElementType)
            {
                continue;
            }

            if (!NuGetVersion.TryParse(variableInfo.VariableValue, out var resolvedVersion))
            {
                continue;
            }

            references.Add(new FileBasedAppReference
            {
                Name = variableInfo.PackageName,
                ResolvedVersion = resolvedVersion,
                VersionRange = FileBasedAppReferenceHelper.CreateMinimumVersionRange(resolvedVersion),
                Kind = variableInfo.ElementType == PackageVariableInfo.FileBasedSdkDirectiveElementType
                    ? FileBasedAppReferenceKind.Sdk
                    : FileBasedAppReferenceKind.Package,
                NameExpression = variableInfo.PackageReferenceName,
                VersionExpression = variableInfo.PackageReferenceVersion,
                VariableInfo = variableInfo
            });
        }

        return [
            .. references
                .GroupBy(reference => (reference.Kind, Name: reference.Name.ToUpperInvariant()))
                .Select(group => group.FirstOrDefault(reference => reference.VariableInfo != null) ?? group.First())
        ];
    }

    private bool UpdateFileBasedAppVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion, Regex directiveReplaceRegex)
    {
        var updated = false;

        if (_fileSystem.File.Exists(variableInfo.FilePath) && !string.IsNullOrEmpty(variableInfo.VariableName))
        {
            if (variableInfo.FilePath.IsCSharpFile())
            {
                var propertyContent = _fileSystem.File.ReadAllText(variableInfo.FilePath);
                var propertyPattern = $@"(^[ \t]*#:[ \t]*property[ \t]+{Regex.Escape(variableInfo.VariableName)}[ \t]*=[ \t]*)([^\r\n]*)(\r?\n|$)";
                var newPropertyContent = Regex.Replace(
                    propertyContent,
                    propertyPattern,
                    match => match.Groups[1].Value + newVersion + match.Groups[3].Value,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                {
                    _fileSystem.File.WriteAllText(variableInfo.FilePath, newPropertyContent);
                    updated = true;
                }
            }
            else if (TryUpdateXmlPropertyFile(variableInfo.PackageReferenceFilePath, variableInfo.FilePath, variableInfo.VariableName, newVersion.ToString()))
            {
                updated = true;
            }
        }

        if (_fileSystem.File.Exists(variableInfo.PackageReferenceFilePath) && variableInfo.PackageReferenceFilePath.IsCSharpFile())
        {
            var content = _fileSystem.File.ReadAllText(variableInfo.PackageReferenceFilePath);
            var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
            CollectFileBasedAppPropertyDefinitionsForUpdate(variableInfo.PackageReferenceFilePath, content, propertyDefinitions);
            var directiveVersion = FileBasedAppReferenceHelper.ExpressionContainsPropertyReference(variableInfo.PackageReferenceVersion)
                ? variableInfo.PackageReferenceVersion
                : newVersion.ToString();

            var newContent = directiveReplaceRegex.Replace(
                content,
                match =>
                {
                    var directive = match.Groups[2].Value;
                    if (!TryParsePackageDirective(directive, out var expression, out _) ||
                        (!string.Equals(expression, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                         (!TryResolveProperties(expression, propertyDefinitions, out var resolvedName) ||
                          !string.Equals(resolvedName, variableInfo.PackageName, StringComparison.OrdinalIgnoreCase))))
                    {
                        return match.Value;
                    }

                    return match.Groups[1].Value + variableInfo.PackageReferenceName + "@" + directiveVersion + match.Groups[3].Value;
                });

            if (!string.Equals(content, newContent, StringComparison.Ordinal))
            {
                _fileSystem.File.WriteAllText(variableInfo.PackageReferenceFilePath, newContent);
                updated = true;
            }
        }

        return updated;
    }

    private void InvalidateCache(string propertyFilePath, string packageRefFilePath)
    {
        var keysToRemove = _cache.Keys.Where(key =>
        {
            if (_cache.TryGetValue(key, out var cachedVariables) &&
                cachedVariables.Values.Any(v =>
                    string.Equals(v.FilePath, propertyFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.FilePath, packageRefFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.PackageReferenceFilePath, propertyFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.PackageReferenceFilePath, packageRefFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var projectFile = _fileSystem.FileInfo.New(key);
            if (!projectFile.Exists) return false;

            var directory = projectFile.Directory;
            while (directory != null)
            {
                var propertyFileInDir = !string.IsNullOrEmpty(propertyFilePath) &&
                    _fileSystem.Path.Combine(directory.FullName, _fileSystem.Path.GetFileName(propertyFilePath))
                        .Equals(propertyFilePath, StringComparison.OrdinalIgnoreCase);
                var packageFileInDir = !string.IsNullOrEmpty(packageRefFilePath) &&
                    _fileSystem.Path.Combine(directory.FullName, _fileSystem.Path.GetFileName(packageRefFilePath))
                        .Equals(packageRefFilePath, StringComparison.OrdinalIgnoreCase);

                if (propertyFileInDir || packageFileInDir)
                {
                    return true;
                }
                directory = directory.Parent;
            }
            return false;
        }).ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
            _fileBasedAppScanCache.TryRemove(key, out _);
        }
    }

    public Dictionary<string, PackageVariableInfo> DiscoverPackageVariables(string projectFilePath)
    {
        if (_cache.TryGetValue(projectFilePath, out var cachedResult))
        {
            return cachedResult;
        }

        var result = new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);

        var projectFile = _fileSystem.FileInfo.New(projectFilePath);
        if (!projectFile.Exists)
        {
            _cache[projectFilePath] = result;
            return result;
        }

        if (projectFilePath.IsCSharpFile())
        {
            return GetOrScanFileBasedApp(projectFilePath).PackageVariables;
        }

        result = BuildPackageVariables(EvaluateProject(projectFilePath, includeProjectFile: true));
        _cache[projectFilePath] = result;
        return result;
    }

    // Properties visible to a file-based app from Directory.Build.props and friends (raw values, for chain resolution).
    private void CollectEvaluatedProperties(string projectFilePath, Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        foreach (var (name, definition) in EvaluateProject(projectFilePath, includeProjectFile: false).Properties)
        {
            propertyDefinitions[name] = (definition.RawValue, definition.FilePath);
        }
    }

    private FileBasedAppScanResult GetOrScanFileBasedApp(string projectFilePath)
    {
        if (_fileBasedAppScanCache.TryGetValue(projectFilePath, out var cachedScan))
        {
            return cachedScan;
        }

        var packageVariables = new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);
        var literalReferences = new List<FileBasedAppReference>();
        var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);

        CollectEvaluatedProperties(projectFilePath, propertyDefinitions);

        var lines = _fileSystem.File.ReadLines(projectFilePath).ToList();
        foreach (var line in lines)
        {
            CollectFileBasedAppPropertyFromLine(line, projectFilePath, propertyDefinitions);
        }

        foreach (var line in lines)
        {
            var packageMatch = PackageDirectiveLineRegex().Match(line);
            if (packageMatch.Success)
            {
                ScanFileBasedAppPackageLine(packageMatch.Groups[1].Value, projectFilePath, propertyDefinitions, packageVariables);
                if (TryCreateFileBasedAppReference(
                        packageMatch.Groups[1].Value,
                        propertyDefinitions,
                        FileBasedAppReferenceKind.Package,
                        null,
                        out var packageReference))
                {
                    literalReferences.Add(packageReference);
                }

                continue;
            }

            var sdkMatch = SdkDirectiveLineRegex().Match(line);
            if (sdkMatch.Success)
            {
                ScanFileBasedAppSdkLine(sdkMatch.Groups[1].Value, projectFilePath, propertyDefinitions, packageVariables);
                if (TryCreateFileBasedAppReference(
                        sdkMatch.Groups[1].Value,
                        propertyDefinitions,
                        FileBasedAppReferenceKind.Sdk,
                        null,
                        out var sdkReference))
                {
                    literalReferences.Add(sdkReference);
                }
            }
        }

        var scanResult = new FileBasedAppScanResult(packageVariables, literalReferences);
        _fileBasedAppScanCache[projectFilePath] = scanResult;
        _cache[projectFilePath] = packageVariables;
        return scanResult;
    }

    private void CollectFileBasedAppPropertyDefinitionsForUpdate(
        string projectFilePath,
        string content,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        CollectEvaluatedProperties(projectFilePath, propertyDefinitions);
        CollectFileBasedAppPropertyDefinitionsFromContent(content, projectFilePath, propertyDefinitions);
    }

    private static void CollectFileBasedAppPropertyDefinitionsFromContent(
        string content,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        using var reader = new StringReader(content);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            CollectFileBasedAppPropertyFromLine(line, filePath, propertyDefinitions);
        }
    }

    private static void CollectFileBasedAppPropertyFromLine(
        string line,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        var match = PropertyDirectiveLineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var propertyName = match.Groups[1].Value;
        // Later #:property lines in the same file override earlier ones (MSBuild-like semantics).
        propertyDefinitions[propertyName] = (match.Groups[2].Value, filePath);
    }

    private static void ScanFileBasedAppPackageLine(
        string directive,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        Dictionary<string, PackageVariableInfo> result)
    {
        if (!TryParsePackageDirective(directive, out var packageExpression, out var versionExpression) ||
            !TryResolveProperties(packageExpression, propertyDefinitions, out var packageName) ||
            result.ContainsKey(packageName))
        {
            return;
        }

        var versionMatch = MsBuildPropertyReferenceRegex().Match(versionExpression);
        if (!versionMatch.Success ||
            !TryResolvePropertyChain(versionMatch.Groups[1].Value, propertyDefinitions, out var rootVariableName, out var propertyInfo))
        {
            return;
        }

        result[packageName] = new PackageVariableInfo
        {
            PackageName = packageName,
            VariableName = rootVariableName,
            VariableValue = propertyInfo.Value,
            FilePath = propertyInfo.FilePath,
            PackageReferenceFilePath = filePath,
            ElementType = PackageVariableInfo.FileBasedPackageDirectiveElementType,
            PackageReferenceName = packageExpression,
            PackageReferenceVersion = versionExpression
        };
    }

    private static void ScanFileBasedAppSdkLine(
        string directive,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        Dictionary<string, PackageVariableInfo> result)
    {
        if (!TryParsePackageDirective(directive, out var sdkExpression, out var versionExpression) ||
            !TryResolveProperties(sdkExpression, propertyDefinitions, out var sdkName) ||
            result.ContainsKey(sdkName))
        {
            return;
        }

        var versionMatch = MsBuildPropertyReferenceRegex().Match(versionExpression);
        if (versionMatch.Success)
        {
            if (TryResolvePropertyChain(versionMatch.Groups[1].Value, propertyDefinitions, out var rootVariableName, out var propertyInfo))
            {
                result[sdkName] = new PackageVariableInfo
                {
                    PackageName = sdkName,
                    VariableName = rootVariableName,
                    VariableValue = propertyInfo.Value,
                    FilePath = propertyInfo.FilePath,
                    PackageReferenceFilePath = filePath,
                    ElementType = PackageVariableInfo.FileBasedSdkDirectiveElementType,
                    PackageReferenceName = sdkExpression,
                    PackageReferenceVersion = versionExpression
                };
            }

            return;
        }

        if (FileBasedAppReferenceHelper.ExpressionContainsPropertyReference(sdkExpression) &&
            NuGetVersion.TryParse(versionExpression, out _))
        {
            result[sdkName] = new PackageVariableInfo
            {
                PackageName = sdkName,
                VariableName = string.Empty,
                VariableValue = versionExpression,
                FilePath = filePath,
                PackageReferenceFilePath = filePath,
                ElementType = PackageVariableInfo.FileBasedSdkDirectiveElementType,
                PackageReferenceName = sdkExpression,
                PackageReferenceVersion = versionExpression
            };
        }
    }

    private sealed class FileBasedAppScanResult
    {
        public FileBasedAppScanResult(
            Dictionary<string, PackageVariableInfo> packageVariables,
            List<FileBasedAppReference> literalReferences)
        {
            PackageVariables = packageVariables;
            LiteralReferences = literalReferences;
        }

        public Dictionary<string, PackageVariableInfo> PackageVariables { get; }

        public List<FileBasedAppReference> LiteralReferences { get; }
    }

    private static bool TryResolvePropertyChain(
        string variableName,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        out string resolvedVariableName,
        out (string Value, string FilePath) propertyInfo)
    {
        resolvedVariableName = variableName;
        if (!propertyDefinitions.TryGetValue(variableName, out propertyInfo))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { variableName };
        while (TryGetSinglePropertyReference(propertyInfo.Value, out var nextVariable) &&
               visited.Add(nextVariable) &&
               propertyDefinitions.TryGetValue(nextVariable, out var nextPropertyInfo))
        {
            resolvedVariableName = nextVariable;
            propertyInfo = nextPropertyInfo;
        }

        return true;
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

    private static string GetAttributeOrElementValue(XElement element, string name) =>
        GetAttributeValue(element, name) ??
        element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static string GetAttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool TryCreateFileBasedAppReference(
        string directive,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        FileBasedAppReferenceKind kind,
        PackageVariableInfo variableInfo,
        out FileBasedAppReference reference)
    {
        reference = null!;
        if (!TryParsePackageDirective(directive, out var nameExpression, out var versionExpression) ||
            !TryResolveProperties(nameExpression, propertyDefinitions, out var name) ||
            !TryResolveProperties(versionExpression, propertyDefinitions, out var versionString) ||
            !NuGetVersion.TryParse(versionString, out var resolvedVersion))
        {
            return false;
        }

        reference = new FileBasedAppReference
        {
            Name = name,
            ResolvedVersion = resolvedVersion,
            VersionRange = FileBasedAppReferenceHelper.CreateMinimumVersionRange(resolvedVersion),
            Kind = kind,
            NameExpression = nameExpression,
            VersionExpression = versionExpression,
            VariableInfo = variableInfo
        };
        return true;
    }

    private static bool TryParsePackageDirective(string directive, out string packageExpression, out string versionExpression)
    {
        packageExpression = string.Empty;
        versionExpression = string.Empty;

        var token = directive.Trim();
        var whitespaceIndex = token.IndexOfAny([' ', '\t']);
        if (whitespaceIndex >= 0)
        {
            token = token[..whitespaceIndex];
        }

        var separatorIndex = token.LastIndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
        {
            return false;
        }

        packageExpression = token[..separatorIndex].Trim();
        versionExpression = token[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrEmpty(packageExpression) && !string.IsNullOrEmpty(versionExpression);
    }

    private static bool TryResolveProperties(string expression, Dictionary<string, (string Value, string FilePath)> propertyDefinitions, out string resolvedValue)
    {
        var current = expression;
        for (var depth = 0; depth < 10; depth++)
        {
            var unresolved = false;
            var changed = false;
            current = MsBuildPropertyReferenceRegex().Replace(current, match =>
            {
                if (propertyDefinitions.TryGetValue(match.Groups[1].Value, out var propertyInfo))
                {
                    changed = true;
                    return propertyInfo.Value;
                }

                unresolved = true;
                return match.Value;
            });

            if (unresolved)
            {
                resolvedValue = current;
                return false;
            }

            if (!changed || !MsBuildPropertyReferenceRegex().IsMatch(current))
            {
                resolvedValue = current;
                return true;
            }
        }

        resolvedValue = current;
        return !MsBuildPropertyReferenceRegex().IsMatch(current);
    }
}

public class PackageVariableInfo
{
    public const string FileBasedPackageDirectiveElementType = "FileBasedPackageDirective";

    public const string FileBasedSdkDirectiveElementType = "FileBasedSdkDirective";

    public string PackageName { get; set; }
    public string VariableName { get; set; }
    public string VariableValue { get; set; }
    public string FilePath { get; set; }  // Where the property is DEFINED
    public string PackageReferenceFilePath { get; set; }  // Where the PackageReference/PackageVersion is USED
    public string ElementType { get; set; }
    public string PackageReferenceName { get; set; }
    public string PackageReferenceVersion { get; set; }
    public bool IsVersionOverride { get; set; }

    /// <summary>
    /// Why the variable can't be updated safely (for example an unsupported version expression, or a property
    /// assigned under a condition that can't be evaluated statically); <see langword="null"/> when it can.
    /// </summary>
    public string UnsupportedReason { get; set; }

    /// <summary>Which same-named property element in <see cref="FilePath"/> is in effect; -1 when unknown.</summary>
    internal int PropertyElementIndex { get; set; } = -1;

    /// <summary>The variable isn't defined in any evaluated file and must be added to <see cref="FilePath"/>.</summary>
    internal bool InsertProperty { get; set; }

    /// <summary>The variable is SDK-defined and read by this alias property: add an override just before it.</summary>
    internal (string Name, int Index)? InsertBeforeProperty { get; set; }
}
