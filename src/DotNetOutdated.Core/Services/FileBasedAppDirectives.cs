using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// The <c>#:package</c>, <c>#:sdk</c> and <c>#:property</c> directives of a .NET file-based app: which references
/// they declare, and how to change their versions. Properties come from the app's own <c>#:property</c> lines and
/// from the Directory.* files MSBuild would import for it. Edits keep the file's encoding and byte order mark.
/// </summary>
internal sealed partial class FileBasedAppDirectives(IFileSystem fileSystem, IProjectEvaluator evaluator, Action<string>? onWarning = null)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProjectEvaluator _evaluator = evaluator;
    private readonly Action<string>? _onWarning = onWarning;

    [GeneratedRegex(@"^[ \t]*#:[ \t]*package[ \t]+(.+?)\s*$")]
    private static partial Regex PackageDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*sdk[ \t]+(.+?)\s*$")]
    private static partial Regex SdkDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*property[ \t]+([^=\s]+)[ \t]*=[ \t]*(.*?)\s*$")]
    private static partial Regex PropertyDirectiveLineRegex();

    [GeneratedRegex(@"(^[ \t]*#:[ \t]*package[ \t]+)([^\r\n]*)(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex PackageDirectiveReplaceRegex();

    [GeneratedRegex(@"(^[ \t]*#:[ \t]*sdk[ \t]+)([^\r\n]*)(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex SdkDirectiveReplaceRegex();

    /// <summary>
    /// Every <c>#:package</c> and <c>#:sdk</c> reference the app declares whose version resolves, literal or
    /// property-backed. A property-backed reference carries its <see cref="FileBasedAppReference.VariableInfo"/>.
    /// </summary>
    public IReadOnlyList<FileBasedAppReference> References(string projectFilePath)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        var scan = Scan(projectFilePath);
        var references = new List<FileBasedAppReference>(scan.LiteralReferences);

        foreach (var variableInfo in scan.PackageVariables.Values.Concat(scan.SdkVariables.Values))
        {
            if (!variableInfo.IsFileBasedDirective ||
                !NuGetVersion.TryParse(variableInfo.VariableValue, out var resolvedVersion))
            {
                continue;
            }

            references.Add(new FileBasedAppReference
            {
                Name = variableInfo.PackageName,
                ResolvedVersion = resolvedVersion,
                VersionRange = FileBasedAppReferenceHelper.CreateMinimumVersionRange(resolvedVersion),
                Kind = variableInfo.ReferenceKind,
                NameExpression = variableInfo.PackageReferenceName ?? string.Empty,
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

    /// <summary>
    /// The property behind each property-backed <c>#:package</c> or <c>#:sdk</c> directive version, keyed by the
    /// resolved id. Packages and SDKs are kept apart, since an app can declare a package and an SDK with the same id.
    /// </summary>
    public Dictionary<string, PackageVariableInfo> PackageVariables(string projectFilePath, ReferenceKind kind)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);
        }

        var scan = Scan(projectFilePath);
        return kind == ReferenceKind.Sdk ? scan.SdkVariables : scan.PackageVariables;
    }

    /// <summary>Rewrites the version of a literal <c>#:package Id@Version</c> or <c>#:sdk Id@Version</c> directive.</summary>
    /// <returns><see langword="true"/> when the directive line was changed.</returns>
    public bool TryUpdateDirectReference(string projectFilePath, string name, ReferenceKind kind, NuGetVersion newVersion)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return false;
        }

        var directiveName = kind == ReferenceKind.Sdk ? "sdk" : "package";
        var file = EncodedText.Read(_fileSystem, projectFilePath);
        var content = file.Content;
        var updatedContent = Regex.Replace(
            content,
            $@"(^[ \t]*#:[ \t]*{directiveName}[ \t]+{Regex.Escape(name)}@)([^\s\r\n]+)(.*?)(\r?\n|$)",
            match => $"{match.Groups[1].Value}{newVersion}{match.Groups[3].Value}{match.Groups[4].Value}",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (string.Equals(content, updatedContent, StringComparison.Ordinal))
        {
            return false;
        }

        file.Write(_fileSystem, projectFilePath, updatedContent);
        return true;
    }

    /// <summary>
    /// Sets the property behind a property-backed directive (a <c>#:property</c> line or a Directory.* property) to
    /// <paramref name="newVersion"/>, and makes the directive read it again.
    /// </summary>
    /// <returns><see langword="true"/> when any file was changed.</returns>
    public bool TryUpdateVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        // A failure after the property was rewritten (say, evaluating the imported properties for the directive pass)
        // still reports the update that was written.
        var updated = false;
        try
        {
            UpdateVariable(variableInfo, newVersion, ref updated);
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke(
                $"Failed to update package reference '{variableInfo.PackageName}' ({variableInfo.ElementType}): {ex.Message}");
        }

        return updated;
    }

    private void UpdateVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion, ref bool updated)
    {
        var directiveReplaceRegex = variableInfo.ReferenceKind == ReferenceKind.Sdk
            ? SdkDirectiveReplaceRegex()
            : PackageDirectiveReplaceRegex();

        if (_fileSystem.File.Exists(variableInfo.FilePath) && !string.IsNullOrEmpty(variableInfo.VariableName))
        {
            if (variableInfo.FilePath.IsCSharpFile())
            {
                var propertyFile = EncodedText.Read(_fileSystem, variableInfo.FilePath);
                var propertyContent = propertyFile.Content;
                var propertyPattern = $@"(^[ \t]*#:[ \t]*property[ \t]+{Regex.Escape(variableInfo.VariableName)}[ \t]*=[ \t]*)([^\r\n]*)(\r?\n|$)";
                var newPropertyContent = Regex.Replace(
                    propertyContent,
                    propertyPattern,
                    match => match.Groups[1].Value + newVersion + match.Groups[3].Value,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                {
                    propertyFile.Write(_fileSystem, variableInfo.FilePath, newPropertyContent);
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
            var file = EncodedText.Read(_fileSystem, variableInfo.PackageReferenceFilePath);
            var content = file.Content;
            var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
            CollectEvaluatedProperties(variableInfo.PackageReferenceFilePath, propertyDefinitions);
            CollectPropertyDefinitionsFromContent(content, variableInfo.PackageReferenceFilePath, propertyDefinitions);
            var directiveVersion = VersionExpression.ContainsPropertyReference(variableInfo.PackageReferenceVersion)
                ? variableInfo.PackageReferenceVersion
                : newVersion.ToString();

            var newContent = directiveReplaceRegex.Replace(
                content,
                match =>
                {
                    var directive = match.Groups[2].Value;
                    if (!TryParseDirective(directive, out var expression, out _) ||
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
                file.Write(_fileSystem, variableInfo.PackageReferenceFilePath, newContent);
                updated = true;
            }
        }
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

        var properties = _evaluator.GetImportedProperties(projectFilePath);
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

    private sealed record ScanResult(
        Dictionary<string, PackageVariableInfo> PackageVariables,
        Dictionary<string, PackageVariableInfo> SdkVariables,
        List<FileBasedAppReference> LiteralReferences);

    private ScanResult Scan(string projectFilePath)
    {
        var packageVariables = new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);
        var sdkVariables = new Dictionary<string, PackageVariableInfo>(StringComparer.OrdinalIgnoreCase);
        var literalReferences = new List<FileBasedAppReference>();
        var propertyDefinitions = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);

        CollectEvaluatedProperties(projectFilePath, propertyDefinitions);

        var lines = _fileSystem.File.ReadLines(projectFilePath).ToList();
        foreach (var line in lines)
        {
            CollectPropertyFromLine(line, projectFilePath, propertyDefinitions);
        }

        foreach (var line in lines)
        {
            var packageMatch = PackageDirectiveLineRegex().Match(line);
            if (packageMatch.Success)
            {
                ScanPackageLine(packageMatch.Groups[1].Value, projectFilePath, propertyDefinitions, packageVariables);
                if (TryCreateReference(packageMatch.Groups[1].Value, propertyDefinitions, ReferenceKind.Package, out var packageReference))
                {
                    literalReferences.Add(packageReference);
                }

                continue;
            }

            var sdkMatch = SdkDirectiveLineRegex().Match(line);
            if (sdkMatch.Success)
            {
                ScanSdkLine(sdkMatch.Groups[1].Value, projectFilePath, propertyDefinitions, sdkVariables);
                if (TryCreateReference(sdkMatch.Groups[1].Value, propertyDefinitions, ReferenceKind.Sdk, out var sdkReference))
                {
                    literalReferences.Add(sdkReference);
                }
            }
        }

        return new ScanResult(packageVariables, sdkVariables, literalReferences);
    }

    // Properties visible to a file-based app from Directory.Build.props and friends (raw values, for chain resolution).
    private void CollectEvaluatedProperties(string projectFilePath, Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        foreach (var (name, definition) in _evaluator.GetImportedProperties(projectFilePath))
        {
            propertyDefinitions[name] = (definition.RawValue, definition.FilePath);
        }
    }

    private static void CollectPropertyDefinitionsFromContent(
        string content,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            CollectPropertyFromLine(line, filePath, propertyDefinitions);
        }
    }

    private static void CollectPropertyFromLine(
        string line,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        var match = PropertyDirectiveLineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        // Later #:property lines in the same file override earlier ones (MSBuild-like semantics).
        propertyDefinitions[match.Groups[1].Value] = (match.Groups[2].Value, filePath);
    }

    private static void ScanPackageLine(
        string directive,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        Dictionary<string, PackageVariableInfo> result)
    {
        if (!TryParseDirective(directive, out var packageExpression, out var versionExpression) ||
            !TryResolveProperties(packageExpression, propertyDefinitions, out var packageName) ||
            result.ContainsKey(packageName))
        {
            return;
        }

        var versionMatch = VersionExpression.PropertyReferenceRegex().Match(versionExpression);
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

    private static void ScanSdkLine(
        string directive,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        Dictionary<string, PackageVariableInfo> result)
    {
        if (!TryParseDirective(directive, out var sdkExpression, out var versionExpression) ||
            !TryResolveProperties(sdkExpression, propertyDefinitions, out var sdkName) ||
            result.ContainsKey(sdkName))
        {
            return;
        }

        var versionMatch = VersionExpression.PropertyReferenceRegex().Match(versionExpression);
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

        if (VersionExpression.ContainsPropertyReference(sdkExpression) &&
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
        while (VersionExpression.TryGetSinglePropertyReference(propertyInfo.Value, out var nextVariable) &&
               visited.Add(nextVariable) &&
               propertyDefinitions.TryGetValue(nextVariable, out var nextPropertyInfo))
        {
            resolvedVariableName = nextVariable;
            propertyInfo = nextPropertyInfo;
        }

        return true;
    }

    private static bool TryCreateReference(
        string directive,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        ReferenceKind kind,
        [NotNullWhen(true)] out FileBasedAppReference? reference)
    {
        reference = null;
        if (!TryParseDirective(directive, out var nameExpression, out var versionExpression) ||
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
        };
        return true;
    }

    private static bool TryParseDirective(string directive, out string packageExpression, out string versionExpression)
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
            current = VersionExpression.PropertyReferenceRegex().Replace(current, match =>
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

            if (!changed || !VersionExpression.PropertyReferenceRegex().IsMatch(current))
            {
                resolvedValue = current;
                return true;
            }
        }

        resolvedValue = current;
        return !VersionExpression.PropertyReferenceRegex().IsMatch(current);
    }
}
