using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    /// Clears the internal cache. Useful for testing or when you know files have changed.
    /// </summary>
    void ClearCache();
}

// <summary>
// This service provides a simple implementation for tracking and updating versions that are defined using variables in MSBuild project files. 
// It scans the project file and its imports for any PackageReference elements that use variable references for their version, and allows updating those variables while preserving the variable reference syntax in the project file. 
// This is a limited implementation that focuses on variables defined in the same file as the PackageReference or PackageVersion, and does not handle more complex scenarios like variables defined in other files or conditional imports. 
// It also uses regex to update the XML content while preserving formatting, which may not cover all edge cases. However, it should work for common scenarios.
// </summary>
public sealed class VariableTrackingService : IVariableTrackingService
{
    private readonly IFileSystem _fileSystem;
    private readonly Dictionary<string, Dictionary<string, PackageVariableInfo>> _cache;

    public VariableTrackingService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _cache = new Dictionary<string, Dictionary<string, PackageVariableInfo>>(StringComparer.OrdinalIgnoreCase);
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    public void UpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        try
        {
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
                    propertyContent = Regex.Replace(propertyContent, pattern, replacement);
                    _fileSystem.File.WriteAllText(propertyFilePath, propertyContent);
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
                        packageRefContent = Regex.Replace(packageRefContent, packagePattern, packageReplacement, RegexOptions.IgnoreCase);
                    }
                }

                _fileSystem.File.WriteAllText(packageRefFilePath, packageRefContent);
            }

            // Invalidate cache for the affected project since we modified files
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
            }
        }
        catch
        {
            // Silently ignore errors - the package was still upgraded, just without variable reference preservation
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
                var match = Regex.Match(versionValue, @"\$\(([^)]+)\)");
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
}

public class PackageVariableInfo
{
    public string PackageName { get; set; }
    public string VariableName { get; set; }
    public string VariableValue { get; set; }
    public string FilePath { get; set; }  // Where the property is DEFINED
    public string PackageReferenceFilePath { get; set; }  // Where the PackageReference/PackageVersion is USED
    public string ElementType { get; set; }
}