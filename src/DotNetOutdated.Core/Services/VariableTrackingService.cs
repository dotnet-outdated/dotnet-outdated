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
        return TryUpdatePackageVariable(variableInfo, newVersion, newVersion.ToString());
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

        return TryUpdatePackageVariable(variableInfo, newVersionRange.MinVersion, newVersionRange.ToVersionString());
    }

    internal static bool IsSinglePropertyReference(string expression) =>
        string.IsNullOrWhiteSpace(expression) || TryGetSinglePropertyReference(expression, out _);

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

    // newVersion is used for file-based app variables and range-wrapped variable references; newMSBuildValue is written to MSBuild properties.
    private bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion, string newMSBuildValue)
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

            var propertyValueToWrite = IsSinglePropertyReference(variableInfo.PackageReferenceVersion)
                ? newMSBuildValue
                : newVersion.ToString();

            // Step 1: Update the property value in the file where it's defined (or insert it if implicit/SDK-defined)
            string propertyFilePath = variableInfo.FilePath;
            var msbuildUpdated = TryUpdateXmlPropertyFile(propertyFilePath, variableInfo.VariableName, propertyValueToWrite, insertIfMissing: true);

            // Step 2: Restore the variable reference in the PackageReference file (might be different from property file)
            string packageRefFilePath = variableInfo.PackageReferenceFilePath;
            if (_fileSystem.File.Exists(packageRefFilePath))
            {
                var packageRefFile = EncodedText.Read(_fileSystem, packageRefFilePath);
                string packageRefContent = packageRefFile.Content;
                _ = XDocument.Parse(packageRefContent);

                var versionExpression = !string.IsNullOrEmpty(variableInfo.PackageReferenceVersion)
                    ? variableInfo.PackageReferenceVersion
                    : $"$({variableInfo.VariableName})";

                var newPackageRefContent = PackageVersionMarkup.ReplacePackageVersion(
                    packageRefContent,
                    [variableInfo.ElementType],
                    variableInfo.PackageName,
                    versionExpression);

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

    private bool TryUpdateXmlPropertyFile(string propertyFilePath, string variableName, string newMSBuildValue, bool insertIfMissing)
    {
        if (string.IsNullOrEmpty(propertyFilePath) ||
            string.IsNullOrEmpty(variableName) ||
            !_fileSystem.File.Exists(propertyFilePath))
        {
            return false;
        }

        var propertyFile = EncodedText.Read(_fileSystem, propertyFilePath);
        string propertyContent = propertyFile.Content;
        var propertyDoc = XDocument.Parse(propertyContent);

        var propertyElement = propertyDoc.Descendants()
            .LastOrDefault(e => e.Name.LocalName.Equals(variableName, StringComparison.OrdinalIgnoreCase) &&
                                e.Parent?.Name.LocalName == "PropertyGroup");

        string newPropertyContent;
        if (propertyElement != null)
        {
            newPropertyContent = PackageVersionMarkup.ReplacePropertyValue(
                propertyContent,
                variableName,
                propertyElement.Value,
                newMSBuildValue);
        }
        else if (insertIfMissing)
        {
            newPropertyContent = PackageVersionMarkup.InsertProperty(
                propertyContent,
                variableName,
                newMSBuildValue);
        }
        else
        {
            return false;
        }

        if (string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
        {
            return false;
        }

        propertyFile.Write(_fileSystem, propertyFilePath, newPropertyContent);
        return true;
    }

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
            else if (TryUpdateXmlPropertyFile(variableInfo.FilePath, variableInfo.VariableName, newVersion.ToString(), insertIfMissing: false))
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

        var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
        var filesToScan = CollectFilesToScan(projectFilePath, propertyDefinitions, includeProjectFile: true);

        foreach (var fileToScan in filesToScan)
        {
            ScanFileForVariables(fileToScan, result, propertyDefinitions);
        }

        _cache[projectFilePath] = result;
        return result;
    }

    private List<string> CollectFilesToScan(
        string projectFilePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        bool includeProjectFile)
    {
        var filesToScan = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (includeProjectFile && _fileSystem.File.Exists(projectFilePath))
        {
            filesToScan.Add(projectFilePath);
            seenFiles.Add(_fileSystem.Path.GetFullPath(projectFilePath));
        }

        var projectFile = _fileSystem.FileInfo.New(projectFilePath);
        var projectDirectory = projectFile.DirectoryName ?? string.Empty;
        var directory = projectFile.Directory;
        while (directory != null)
        {
            foreach (var propsFile in directory.GetFiles("*.props", SearchOption.TopDirectoryOnly))
            {
                if (seenFiles.Add(_fileSystem.Path.GetFullPath(propsFile.FullName)))
                {
                    filesToScan.Add(propsFile.FullName);
                }
            }
            directory = directory.Parent;
        }

        for (var i = 0; i < filesToScan.Count; i++)
        {
            var importedFiles = CollectPropertyDefinitionsAndImports(filesToScan[i], projectDirectory, propertyDefinitions);
            var insertIndex = i + 1;
            foreach (var importedFile in importedFiles)
            {
                if (seenFiles.Add(importedFile))
                {
                    filesToScan.Insert(insertIndex++, importedFile);
                }
            }
        }

        return filesToScan;
    }

    private List<string> CollectPropertyDefinitionsAndImports(
        string filePath,
        string projectDirectory,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        var importedFiles = new List<string>();
        try
        {
            var doc = XDocument.Parse(_fileSystem.File.ReadAllText(filePath));

            // Within a single file, later property definitions override earlier ones (MSBuild top-to-bottom semantics).
            var fileProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                fileProperties[property.Name.LocalName] = property.Value.Trim();
            }

            // Across files, first file in hierarchy order (csproj -> nearest .props -> parent .props) wins.
            foreach (var (propertyName, propertyValue) in fileProperties)
            {
                propertyDefinitions.TryAdd(propertyName, (propertyValue, filePath));
            }

            var currentDirectory = _fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
            foreach (var import in doc.Descendants().Where(e => e.Name.LocalName == "Import" && GetAttributeValue(e, "Sdk") == null))
            {
                var rawImportPath = GetAttributeValue(import, "Project");
                if (TryResolveImportPath(rawImportPath, currentDirectory, projectDirectory, filePath, propertyDefinitions, out var resolvedPath))
                {
                    importedFiles.Add(resolvedPath);
                }
            }
        }
        catch
        {
            // Silently ignore files that can't be parsed
        }

        return importedFiles;
    }

    private bool TryResolveImportPath(
        string rawImportPath,
        string currentDirectory,
        string projectDirectory,
        string currentFilePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawImportPath))
        {
            return false;
        }

        var separator = _fileSystem.Path.DirectorySeparatorChar;
        string WithTrailingSlash(string dir) => string.IsNullOrEmpty(dir) ? string.Empty : dir.TrimEnd('\\', '/') + separator;

        var unresolved = false;
        var expanded = MsBuildPropertyReferenceRegex().Replace(rawImportPath.Trim(), match =>
        {
            var propName = match.Groups[1].Value;
            if (propName.Equals("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase))
                return WithTrailingSlash(currentDirectory);
            if (propName.Equals("MSBuildProjectDirectory", StringComparison.OrdinalIgnoreCase))
                return projectDirectory;
            if (propName.Equals("MSBuildProjectDirectoryNoRoot", StringComparison.OrdinalIgnoreCase))
                return WithTrailingSlash(projectDirectory);
            if (propName.Equals("MSBuildThisFileName", StringComparison.OrdinalIgnoreCase))
                return _fileSystem.Path.GetFileNameWithoutExtension(currentFilePath);
            if (propertyDefinitions.TryGetValue(propName, out var propInfo))
                return propInfo.Value;

            unresolved = true;
            return match.Value;
        });

        if (unresolved || expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = expanded.Replace('\\', separator).Replace('/', separator);
        var combined = _fileSystem.Path.IsPathRooted(normalized)
            ? normalized
            : _fileSystem.Path.Combine(currentDirectory, normalized);

        resolvedPath = _fileSystem.Path.GetFullPath(combined);
        return _fileSystem.File.Exists(resolvedPath);
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

        _ = CollectFilesToScan(projectFilePath, propertyDefinitions, includeProjectFile: false);

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
        _ = CollectFilesToScan(projectFilePath, propertyDefinitions, includeProjectFile: false);
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

    private void ScanFileForVariables(
        string filePath,
        Dictionary<string, PackageVariableInfo> result,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        try
        {
            var doc = XDocument.Parse(_fileSystem.File.ReadAllText(filePath));
            var packageElements = doc.Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion" or "GlobalPackageReference");

            foreach (var element in packageElements)
            {
                var packageName = GetAttributeValue(element, "Include") ?? GetAttributeValue(element, "Update");
                if (string.IsNullOrEmpty(packageName) ||
                    result.ContainsKey(packageName) ||
                    !TryGetPackageVersionExpression(element, out var versionValue, out var isVersionOverride))
                {
                    continue;
                }

                var match = MsBuildPropertyReferenceRegex().Match(versionValue);
                if (!match.Success)
                {
                    continue;
                }

                var variableName = match.Groups[1].Value;
                var resolved = TryResolvePropertyChain(variableName, propertyDefinitions, out var rootVariableName, out var propertyInfo);

                result[packageName] = new PackageVariableInfo
                {
                    PackageName = packageName,
                    VariableName = resolved ? rootVariableName : variableName,
                    VariableValue = resolved ? propertyInfo.Value : string.Empty,
                    FilePath = resolved ? propertyInfo.FilePath : filePath,
                    PackageReferenceFilePath = filePath,
                    ElementType = element.Name.LocalName,
                    PackageReferenceVersion = versionValue,
                    IsVersionOverride = isVersionOverride
                };
            }
        }
        catch
        {
            // Silently ignore files that can't be parsed
        }
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
}
