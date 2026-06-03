using System;
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
// Limitations: no second-order variable resolution, no conditional property handling.
public sealed partial class VariableTrackingService : IVariableTrackingService
{
    private readonly IFileSystem _fileSystem;
    private readonly Action<string> _onWarning;
    private readonly Dictionary<string, Dictionary<string, PackageVariableInfo>> _cache;
    private readonly Dictionary<string, FileBasedAppScanResult> _fileBasedAppScanCache;

    public VariableTrackingService(IFileSystem fileSystem, Action<string> onWarning = null)
    {
        _fileSystem = fileSystem;
        _onWarning = onWarning;
        _cache = new Dictionary<string, Dictionary<string, PackageVariableInfo>>(StringComparer.OrdinalIgnoreCase);
        _fileBasedAppScanCache = new Dictionary<string, FileBasedAppScanResult>(StringComparer.OrdinalIgnoreCase);
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
        try
        {
            if (variableInfo.ElementType == PackageVariableInfo.FileBasedPackageDirectiveElementType)
            {
                var updated = UpdateFileBasedAppPackageVariable(variableInfo, newVersion);
                if (updated)
                {
                    InvalidateCache(variableInfo.FilePath, variableInfo.PackageReferenceFilePath);
                }

                return updated;
            }

            if (variableInfo.ElementType == PackageVariableInfo.FileBasedSdkDirectiveElementType)
            {
                var updated = UpdateFileBasedAppSdkVariable(variableInfo, newVersion);
                if (updated)
                {
                    InvalidateCache(variableInfo.FilePath, variableInfo.PackageReferenceFilePath);
                }

                return updated;
            }

            var msbuildUpdated = false;

            // Step 1: Update the property value in the file where it's defined
            string propertyFilePath = variableInfo.FilePath;
            if (_fileSystem.File.Exists(propertyFilePath))
            {
                string propertyContent = _fileSystem.File.ReadAllText(propertyFilePath);
                var propertyDoc = XDocument.Parse(propertyContent);

                var propertyElement = propertyDoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == variableInfo.VariableName &&
                               e.Parent?.Name.LocalName == "PropertyGroup");

                if (propertyElement != null)
                {
                    // Update property value using regex to preserve formatting
                    string oldValue = propertyElement.Value;
                    string pattern = $@"<{Regex.Escape(variableInfo.VariableName)}>{Regex.Escape(oldValue)}</{Regex.Escape(variableInfo.VariableName)}>";
                    string replacement = $"<{variableInfo.VariableName}>{newVersion}</{variableInfo.VariableName}>";
                    var newPropertyContent = Regex.Replace(propertyContent, pattern, replacement);
                    if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                    {
                        _fileSystem.File.WriteAllText(propertyFilePath, newPropertyContent);
                        msbuildUpdated = true;
                    }
                }
            }

            // Step 2: Restore the variable reference in the PackageReference file (might be different from property file)
            string packageRefFilePath = variableInfo.PackageReferenceFilePath;
            if (_fileSystem.File.Exists(packageRefFilePath))
            {
                string packageRefContent = _fileSystem.File.ReadAllText(packageRefFilePath);
                var packageRefDoc = XDocument.Parse(packageRefContent);

                var packageElements = packageRefDoc.Descendants()
                    .Where(e => e.Name.LocalName == variableInfo.ElementType &&
                               (e.Attribute("Include")?.Value.Equals(variableInfo.PackageName, StringComparison.OrdinalIgnoreCase) == true ||
                                e.Attribute("Update")?.Value.Equals(variableInfo.PackageName, StringComparison.OrdinalIgnoreCase) == true))
                    .ToList();

                var newPackageRefContent = packageRefContent;
                foreach (var packageElement in packageElements)
                {
                    var versionAttr = packageElement.Attribute("Version");
                    if (versionAttr != null)
                    {
                        // Replace the literal version with the variable reference using regex
                        string variableReference = $"$({variableInfo.VariableName})";

                        // Use regex to replace only the Version attribute value for this specific package
                        string packagePattern = $@"(<{Regex.Escape(variableInfo.ElementType)}\s+(?:Include|Update)=""{Regex.Escape(variableInfo.PackageName)}""\s+Version="")[^""]*("")";
                        string packageReplacement = $"$1{variableReference}$2";
                        newPackageRefContent = Regex.Replace(newPackageRefContent, packagePattern, packageReplacement, RegexOptions.IgnoreCase);
                    }
                }

                if (!string.Equals(packageRefContent, newPackageRefContent, StringComparison.Ordinal))
                {
                    _fileSystem.File.WriteAllText(packageRefFilePath, newPackageRefContent);
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

    private bool UpdateFileBasedAppSdkVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
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
                    RegexOptions.Multiline);
                if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                {
                    _fileSystem.File.WriteAllText(variableInfo.FilePath, newPropertyContent);
                    updated = true;
                }
            }
            else
            {
                var propertyContent = _fileSystem.File.ReadAllText(variableInfo.FilePath);
                var propertyDoc = XDocument.Parse(propertyContent);
                var propertyElement = propertyDoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == variableInfo.VariableName &&
                                         e.Parent?.Name.LocalName == "PropertyGroup");

                if (propertyElement != null)
                {
                    string oldValue = propertyElement.Value;
                    string pattern = $@"<{Regex.Escape(variableInfo.VariableName)}>{Regex.Escape(oldValue)}</{Regex.Escape(variableInfo.VariableName)}>";
                    string replacement = $"<{variableInfo.VariableName}>{newVersion}</{variableInfo.VariableName}>";
                    var newPropertyContent = Regex.Replace(propertyContent, pattern, replacement);
                    if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                    {
                        _fileSystem.File.WriteAllText(variableInfo.FilePath, newPropertyContent);
                        updated = true;
                    }
                }
            }
        }

        if (_fileSystem.File.Exists(variableInfo.PackageReferenceFilePath) && variableInfo.PackageReferenceFilePath.IsCSharpFile())
        {
            var sdkContent = _fileSystem.File.ReadAllText(variableInfo.PackageReferenceFilePath);
            var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
            CollectFileBasedAppPropertyDefinitionsForUpdate(variableInfo.PackageReferenceFilePath, sdkContent, propertyDefinitions);
            var directiveVersion = FileBasedAppReferenceHelper.ExpressionContainsPropertyReference(variableInfo.PackageReferenceVersion)
                ? variableInfo.PackageReferenceVersion
                : newVersion.ToString();

            var newSdkContent = SdkDirectiveReplaceRegex().Replace(
                sdkContent,
                match =>
                {
                    var sdkDirective = match.Groups[2].Value;
                    if (!TryParsePackageDirective(sdkDirective, out var sdkExpression, out _) ||
                        (!string.Equals(sdkExpression, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                         (!TryResolveProperties(sdkExpression, propertyDefinitions, out var sdkName) ||
                          !string.Equals(sdkName, variableInfo.PackageName, StringComparison.OrdinalIgnoreCase))))
                    {
                        return match.Value;
                    }

                    return match.Groups[1].Value + variableInfo.PackageReferenceName + "@" + directiveVersion + match.Groups[3].Value;
                });

            if (!string.Equals(sdkContent, newSdkContent, StringComparison.Ordinal))
            {
                _fileSystem.File.WriteAllText(variableInfo.PackageReferenceFilePath, newSdkContent);
                updated = true;
            }
        }

        return updated;
    }

    private bool UpdateFileBasedAppPackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        var updated = false;

        if (_fileSystem.File.Exists(variableInfo.FilePath))
        {
            if (variableInfo.FilePath.IsCSharpFile())
            {
                var propertyContent = _fileSystem.File.ReadAllText(variableInfo.FilePath);
                var propertyPattern = $@"(^[ \t]*#:[ \t]*property[ \t]+{Regex.Escape(variableInfo.VariableName)}[ \t]*=[ \t]*)([^\r\n]*)(\r?\n|$)";
                var newPropertyContent = Regex.Replace(
                    propertyContent,
                    propertyPattern,
                    match => match.Groups[1].Value + newVersion + match.Groups[3].Value,
                    RegexOptions.Multiline);
                if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                {
                    _fileSystem.File.WriteAllText(variableInfo.FilePath, newPropertyContent);
                    updated = true;
                }
            }
            else
            {
                var propertyContent = _fileSystem.File.ReadAllText(variableInfo.FilePath);
                var propertyDoc = XDocument.Parse(propertyContent);
                var propertyElement = propertyDoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == variableInfo.VariableName &&
                                         e.Parent?.Name.LocalName == "PropertyGroup");

                if (propertyElement != null)
                {
                    string oldValue = propertyElement.Value;
                    string pattern = $@"<{Regex.Escape(variableInfo.VariableName)}>{Regex.Escape(oldValue)}</{Regex.Escape(variableInfo.VariableName)}>";
                    string replacement = $"<{variableInfo.VariableName}>{newVersion}</{variableInfo.VariableName}>";
                    var newPropertyContent = Regex.Replace(propertyContent, pattern, replacement);
                    if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                    {
                        _fileSystem.File.WriteAllText(variableInfo.FilePath, newPropertyContent);
                        updated = true;
                    }
                }
            }
        }

        if (_fileSystem.File.Exists(variableInfo.PackageReferenceFilePath) && variableInfo.PackageReferenceFilePath.IsCSharpFile())
        {
            var packageContent = _fileSystem.File.ReadAllText(variableInfo.PackageReferenceFilePath);
            var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
            CollectFileBasedAppPropertyDefinitionsForUpdate(variableInfo.PackageReferenceFilePath, packageContent, propertyDefinitions);
            var directiveVersion = FileBasedAppReferenceHelper.ExpressionContainsPropertyReference(variableInfo.PackageReferenceVersion)
                ? variableInfo.PackageReferenceVersion
                : newVersion.ToString();

            var newPackageContent = PackageDirectiveReplaceRegex().Replace(
                packageContent,
                match =>
                {
                    var packageDirective = match.Groups[2].Value;
                    if (!TryParsePackageDirective(packageDirective, out var packageExpression, out _) ||
                        (!string.Equals(packageExpression, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                         (!TryResolveProperties(packageExpression, propertyDefinitions, out var packageName) ||
                          !string.Equals(packageName, variableInfo.PackageName, StringComparison.OrdinalIgnoreCase))))
                    {
                        return match.Value;
                    }

                    return match.Groups[1].Value + variableInfo.PackageReferenceName + "@" + directiveVersion + match.Groups[3].Value;
                });

            if (!string.Equals(packageContent, newPackageContent, StringComparison.Ordinal))
            {
                _fileSystem.File.WriteAllText(variableInfo.PackageReferenceFilePath, newPackageContent);
                updated = true;
            }
        }

        return updated;
    }

    private void InvalidateCache(string propertyFilePath, string packageRefFilePath)
    {
        var keysToRemove = _cache.Keys.Where(key =>
        {
            var projectFile = _fileSystem.FileInfo.New(key);
            if (!projectFile.Exists) return false;

            // Check if this project or any parent directory contains the modified files
            var directory = projectFile.Directory;
            while (directory != null)
            {
                var propertyFileInDir = _fileSystem.Path.Combine(directory.FullName, _fileSystem.Path.GetFileName(propertyFilePath))
                    .Equals(propertyFilePath, StringComparison.OrdinalIgnoreCase);
                var packageFileInDir = _fileSystem.Path.Combine(directory.FullName, _fileSystem.Path.GetFileName(packageRefFilePath))
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
            _cache.Remove(key);
            _fileBasedAppScanCache.Remove(key);
        }
    }

    public Dictionary<string, PackageVariableInfo> DiscoverPackageVariables(string projectFilePath)
    {
        // Check cache first
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

        // First, collect all property definitions from all relevant files
        var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);

        // Collect files to scan (project file + all .props files in parent hierarchy)
        var filesToScan = new List<string> { projectFilePath };
        var directory = projectFile.Directory;
        while (directory != null)
        {
            var propsFiles = directory.GetFiles("*.props", SearchOption.TopDirectoryOnly);
            foreach (var propsFile in propsFiles)
            {
                filesToScan.Add(propsFile.FullName);
            }
            directory = directory.Parent;
        }

        // Scan all files for property definitions
        foreach (var fileToScan in filesToScan)
        {
            CollectPropertyDefinitions(fileToScan, propertyDefinitions);
        }

        // Now scan all files for package references that use variables
        foreach (var fileToScan in filesToScan)
        {
            ScanFileForVariables(fileToScan, result, propertyDefinitions);
        }

        // Cache the result
        _cache[projectFilePath] = result;

        return result;
    }

    private void CollectPropertyDefinitions(string filePath, Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        try
        {
            string content = _fileSystem.File.ReadAllText(filePath);
            var doc = XDocument.Parse(content);

            // Find all property elements
            var properties = doc.Descendants()
                .Where(e => e.Parent?.Name.LocalName == "PropertyGroup");

            foreach (var property in properties)
            {
                string propertyName = property.Name.LocalName;
                string propertyValue = property.Value;

                // Only add if not already present (first definition wins, which matches MSBuild behavior)
                if (!propertyDefinitions.ContainsKey(propertyName))
                {
                    propertyDefinitions[propertyName] = (propertyValue, filePath);
                }
            }
        }
        catch
        {
            // Silently ignore files that can't be parsed
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

        var projectFile = _fileSystem.FileInfo.New(projectFilePath);
        var directory = projectFile.Directory;
        while (directory != null)
        {
            foreach (var propsFile in directory.GetFiles("*.props", SearchOption.TopDirectoryOnly))
            {
                CollectPropertyDefinitions(propsFile.FullName, propertyDefinitions);
            }

            directory = directory.Parent;
        }

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
        var projectFile = _fileSystem.FileInfo.New(projectFilePath);
        var directory = projectFile.Directory;
        while (directory != null)
        {
            foreach (var propsFile in directory.GetFiles("*.props", SearchOption.TopDirectoryOnly))
            {
                CollectPropertyDefinitions(propsFile.FullName, propertyDefinitions);
            }

            directory = directory.Parent;
        }

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
        if (!versionMatch.Success || !propertyDefinitions.TryGetValue(versionMatch.Groups[1].Value, out var propertyInfo))
        {
            return;
        }

        result[packageName] = new PackageVariableInfo
        {
            PackageName = packageName,
            VariableName = versionMatch.Groups[1].Value,
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
            if (propertyDefinitions.TryGetValue(versionMatch.Groups[1].Value, out var propertyInfo))
            {
                result[sdkName] = new PackageVariableInfo
                {
                    PackageName = sdkName,
                    VariableName = versionMatch.Groups[1].Value,
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

    private void ScanFileForVariables(string filePath, Dictionary<string, PackageVariableInfo> result, Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        try
        {
            string content = _fileSystem.File.ReadAllText(filePath);
            var doc = XDocument.Parse(content);

            var packageElements = doc.Descendants()
                .Where(e => (e.Name.LocalName == "PackageReference" ||
                            e.Name.LocalName == "PackageVersion" ||
                            e.Name.LocalName == "GlobalPackageReference") &&
                           (e.Attribute("Include") != null || e.Attribute("Update") != null) &&
                           e.Attribute("Version") != null);

            foreach (var element in packageElements)
            {
                var packageName = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
                var versionValue = element.Attribute("Version")?.Value;

                if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(versionValue))
                    continue;

                // Check if version uses a variable reference like $(VariableName)
                var match = MsBuildPropertyReferenceRegex().Match(versionValue);
                if (match.Success)
                {
                    string variableName = match.Groups[1].Value;

                    // Look up the property definition from our collected definitions
                    if (propertyDefinitions.TryGetValue(variableName, out var propertyInfo) && !result.ContainsKey(packageName))
                    {
                        result[packageName] = new PackageVariableInfo
                        {
                            PackageName = packageName,
                            VariableName = variableName,
                            VariableValue = propertyInfo.Value,
                            FilePath = propertyInfo.FilePath,  // Where the property is DEFINED
                            PackageReferenceFilePath = filePath,  // Where the PackageReference is USED
                            ElementType = element.Name.LocalName
                        };
                    }
                }
            }
        }
        catch
        {
            // Silently ignore files that can't be parsed
        }
    }

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
        var separatorIndex = directive.LastIndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == directive.Length - 1)
        {
            packageExpression = string.Empty;
            versionExpression = string.Empty;
            return false;
        }

        packageExpression = directive[..separatorIndex].Trim();
        versionExpression = directive[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrEmpty(packageExpression) && !string.IsNullOrEmpty(versionExpression);
    }

    private static bool TryResolveProperties(string expression, Dictionary<string, (string Value, string FilePath)> propertyDefinitions, out string resolvedValue)
    {
        var unresolved = false;
        resolvedValue = MsBuildPropertyReferenceRegex().Replace(expression, match =>
        {
            if (propertyDefinitions.TryGetValue(match.Groups[1].Value, out var propertyInfo))
            {
                return propertyInfo.Value;
            }

            unresolved = true;
            return match.Value;
        });

        return !unresolved;
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
}
