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

public sealed class VariableTrackingService : IVariableTrackingService
{
    private readonly IFileSystem _fileSystem;
    private readonly Dictionary<string, Dictionary<string, PackageVariableInfo>> _cache;
    
    private static readonly string[] _fileExtensions =
    [
        ".csproj",
        ".fsproj",
        ".props",
    ];

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
            // Determine which file to update
            string fileToUpdate = variableInfo.FilePath;
            
            // Verify the file exists
            if (!_fileSystem.File.Exists(fileToUpdate))
            {
                return;
            }

            string content = _fileSystem.File.ReadAllText(fileToUpdate);
            var doc = XDocument.Parse(content);

            // Update the property value
            var propertyElement = doc.Descendants()
                .Where(e => e.Name.LocalName == variableInfo.VariableName && 
                           e.Parent?.Name.LocalName == "PropertyGroup")
                .FirstOrDefault();

            if (propertyElement != null)
            {
                // Update property value using regex to preserve formatting
                string oldValue = propertyElement.Value;
                string pattern = $@"<{Regex.Escape(variableInfo.VariableName)}>{Regex.Escape(oldValue)}</{Regex.Escape(variableInfo.VariableName)}>";
                string replacement = $"<{variableInfo.VariableName}>{newVersion}</{variableInfo.VariableName}>";
                content = Regex.Replace(content, pattern, replacement);
            }

            // Restore the variable reference in the PackageReference
            // Find the package element and restore the variable syntax
            var packageElements = doc.Descendants()
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
                    content = Regex.Replace(content, packagePattern, packageReplacement, RegexOptions.IgnoreCase);
                }
            }

            _fileSystem.File.WriteAllText(fileToUpdate, content);
            
            // Invalidate cache for the affected project since we modified the file
            // Find and remove any cached entries that might have scanned this file
            var keysToRemove = _cache.Keys.Where(key => 
            {
                var projectFile = _fileSystem.FileInfo.New(key);
                if (!projectFile.Exists) return false;
                
                // Check if this project or any parent directory contains the modified file
                var directory = projectFile.Directory;
                while (directory != null)
                {
                    if (_fileSystem.Path.Combine(directory.FullName, _fileSystem.Path.GetFileName(fileToUpdate))
                        .Equals(fileToUpdate, StringComparison.OrdinalIgnoreCase))
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

        // Scan the project file itself
        ScanFileForVariables(projectFilePath, result);

        // Scan for Directory.Build.props and Directory.Packages.props
        var directory = projectFile.Directory;
        while (directory != null)
        {
            var propsFiles = directory.GetFiles("*.props", SearchOption.TopDirectoryOnly);
            foreach (var propsFile in propsFiles)
            {
                ScanFileForVariables(propsFile.FullName, result);
            }
            
            directory = directory.Parent;
        }

        // Cache the result
        _cache[projectFilePath] = result;

        return result;
    }

    private void ScanFileForVariables(string filePath, Dictionary<string, PackageVariableInfo> result)
    {
        try
        {
            string content = _fileSystem.File.ReadAllText(filePath);
            var doc = XDocument.Parse(content);

            // Find all PackageReference and PackageVersion elements with variable-based versions
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
                var match = System.Text.RegularExpressions.Regex.Match(versionValue, @"\$\(([^)]+)\)");
                if (match.Success)
                {
                    string variableName = match.Groups[1].Value;
                    
                    // Find the property definition
                    var propertyElement = doc.Descendants()
                        .Where(e => e.Name.LocalName == variableName && 
                                   e.Parent?.Name.LocalName == "PropertyGroup")
                        .FirstOrDefault();

                    if (propertyElement != null && !result.ContainsKey(packageName))
                    {
                        result[packageName] = new PackageVariableInfo
                        {
                            PackageName = packageName,
                            VariableName = variableName,
                            VariableValue = propertyElement.Value,
                            FilePath = filePath,
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
    public string FilePath { get; set; }
    public string ElementType { get; set; }
}