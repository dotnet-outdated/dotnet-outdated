using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// The <c>#:package</c>, <c>#:sdk</c> and <c>#:property</c> directives of a .NET file-based app: which references
/// they declare, and how to change their versions. The directives are read from every C# file the app compiles: its
/// entry point and the files <c>#:include</c> directives add to it. Properties come from those files'
/// <c>#:property</c> lines and from the Directory.* files MSBuild would import for the app. Edits keep each file's
/// encoding and byte order mark.
/// </summary>
internal sealed partial class FileBasedAppDirectives(IFileSystem fileSystem, IProjectEvaluator evaluator, Action<string>? onWarning = null)
{
    // A version after `@`: quoted (as `dotnet package add` writes a range holding a space), a range that may hold a
    // space, or a bare token, but not Name=Value metadata (`#:package Id@ ExcludeAssets=runtime` has no version).
    private const string VersionTokenPattern = @"(?![A-Za-z_][\w.-]*=)(?:""(?:[^""\\\r\n]|\\.)*""|[\[\(][^\]\)\r\n]*[\]\)]|[^\s]+)";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProjectEvaluator _evaluator = evaluator;
    private readonly Action<string>? _onWarning = onWarning;

    [GeneratedRegex(@"^[ \t]*#:[ \t]*package[ \t]+(.+?)\s*$")]
    private static partial Regex PackageDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*sdk[ \t]+(.+?)\s*$")]
    private static partial Regex SdkDirectiveLineRegex();

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
                FilePath = variableInfo.PackageReferenceFilePath,
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

    /// <summary>
    /// The full paths of the C# files the app compiles: its entry point first, then each <c>.cs</c> file an
    /// <c>#:include</c> directive adds (from the entry point or an included file, relative to the file holding the
    /// directive, with <c>*</c>, <c>?</c> and <c>**</c> globs) that no <c>#:exclude</c> directive removes.
    /// </summary>
    public IReadOnlyList<string> SourceFiles(string projectFilePath)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        return [.. ReadSources(projectFilePath).Files.Select(file => file.Path)];
    }

    /// <summary>
    /// Whether the app has an <c>#:include</c> directive, whether or not the files it names could be found.
    /// <c>dotnet package add</c> copies every included file's directives into the entry point, so the package
    /// directives of such an app are written directly.
    /// </summary>
    public bool IncludesFiles(string projectFilePath) =>
        projectFilePath.IsCSharpFile() &&
        _fileSystem.File.Exists(projectFilePath) &&
        ReadSources(projectFilePath).HasIncludes;

    /// <summary>
    /// The range a literal <c>#:package Id@Version</c> directive declares for <paramref name="name"/>, such as
    /// <c>[12.0.1, 13.0.0)</c> (which <c>dotnet add package</c> writes in quotes when it holds a space), or
    /// <see langword="null"/> when no directive declares one that can be read.
    /// </summary>
    public VersionRange? DeclaredPackageRange(string projectFilePath, string name)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return null;
        }

        foreach (var line in ReadSources(projectFilePath).Files.SelectMany(file => file.Directives))
        {
            var match = PackageDirectiveLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            // A range can hold spaces, so the version is everything after the id rather than one token.
            var directive = match.Groups[1].Value;
            var separatorIndex = directive.IndexOf('@', StringComparison.Ordinal);
            if (separatorIndex <= 0 ||
                !string.Equals(directive[..separatorIndex].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = directive[(separatorIndex + 1)..].Trim();
            if (version.Length == 0)
            {
                return null;
            }

            // The version ends at its closing quote or bracket; a bare version ends at the first whitespace.
            var length = version[0] switch
            {
                '"' => version.IndexOf('"', 1) + 1,
                '[' or '(' => version.IndexOfAny([']', ')']) + 1,
                _ => version.IndexOfAny([' ', '\t']),
            };

            if (length < 0)
            {
                length = version.Length;
            }

            return length > 0 && VersionRange.TryParse(version[..length].Trim('"'), out var range) ? range : null;
        }

        return null;
    }

    /// <summary>
    /// The full paths of the files the app compiles that have a <c>#:package</c> or <c>#:sdk</c> directive for
    /// <paramref name="name"/>, literal or not, in the order the app reads them.
    /// </summary>
    public IReadOnlyList<string> DeclaringFiles(string projectFilePath, ReferenceKind kind, string name)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        var lineRegex = kind == ReferenceKind.Sdk ? SdkDirectiveLineRegex() : PackageDirectiveLineRegex();
        var sources = ReadSources(projectFilePath);
        return [
            .. sources.Files
                .Where(file => file.Directives.Any(line =>
                    lineRegex.Match(line) is { Success: true } match &&
                    FileBasedAppSources.TryResolveProperties(NameExpression(match.Groups[1].Value), sources.Properties, out var resolved) &&
                    string.Equals(resolved, name, StringComparison.OrdinalIgnoreCase)))
                .Select(file => file.Path)
        ];
    }

    /// <summary>
    /// Writes <paramref name="versionText"/> to every <c>#:package</c> or <c>#:sdk</c> directive for
    /// <paramref name="name"/> in the files the app compiles: the SDK rejects the same id declared with different
    /// versions, so a directive repeated in several files is changed in each. When no file declares a package, a
    /// directive for it is added to the entry point. Nothing is written when a directive for it can't be rewritten (it
    /// has no version, or names the id through a property), or when an <c>#:include</c> could not be followed (a file
    /// that wasn't read may declare it too).
    /// </summary>
    public DirectiveWrite WriteDirective(string projectFilePath, ReferenceKind kind, string name, string versionText)
    {
        var failure = $"Failed to update file-based app '{projectFilePath}' for '{name}'";
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return DirectiveWrite.Failed($"{failure}: the file was not found.");
        }

        var sources = ReadSources(projectFilePath);
        if (UnreadIncludes(sources) is { } unread)
        {
            return DirectiveWrite.Failed($"{failure}: {unread}");
        }

        var directiveName = kind == ReferenceKind.Sdk ? "sdk" : "package";
        var lineRegex = kind == ReferenceKind.Sdk ? SdkDirectiveLineRegex() : PackageDirectiveLineRegex();
        foreach (var source in sources.Files)
        {
            foreach (var line in source.Directives)
            {
                // Rewriting the literal id below would miss this directive, and adding one would declare the id twice.
                if (lineRegex.Match(line) is { Success: true } match &&
                    NameExpression(match.Groups[1].Value) is var expression &&
                    VersionExpression.ContainsPropertyReference(expression) &&
                    FileBasedAppSources.TryResolveProperties(expression, sources.Properties, out var resolved) &&
                    string.Equals(resolved, name, StringComparison.OrdinalIgnoreCase))
                {
                    return DirectiveWrite.Failed(
                        $"{failure}: '{line.Trim()}' in '{source.Path}' names it through a property; change the version by hand.");
                }
            }
        }

        var directiveRegex = DirectiveRegex(directiveName, name);
        var directiveVersion = versionText.Any(char.IsWhiteSpace) ? $"\"{versionText}\"" : versionText;
        var edits = new List<(string Path, EncodedText File, string Content)>();
        var declaringFiles = new List<string>();
        foreach (var source in sources.Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            string? versionless = null;
            var declares = false;
            var content = FileBasedAppSources.ReplaceInDirectives(file.Content, directiveRegex, match =>
            {
                declares = true;
                if (!match.Groups[3].Success)
                {
                    versionless = match.Value.Trim();
                    return match.Value;
                }

                return match.Groups[1].Value + match.Groups[2].Value + directiveVersion;
            });

            if (versionless != null)
            {
                return DirectiveWrite.Failed($"{failure}: '{versionless}' in '{source.Path}' has no version to update.");
            }

            if (declares)
            {
                declaringFiles.Add(source.Path);
            }

            if (!string.Equals(content, file.Content, StringComparison.Ordinal))
            {
                edits.Add((source.Path, file, content));
            }
        }

        if (declaringFiles.Count == 0)
        {
            if (kind == ReferenceKind.Sdk)
            {
                return DirectiveWrite.Failed(
                    $"{failure}. Expected a literal #:sdk {name}@<version> directive (id match is case-insensitive; trailing comments are preserved).");
            }

            var entryPoint = sources.Files[0].Path;
            var file = EncodedText.Read(_fileSystem, entryPoint);
            edits.Add((entryPoint, file, FileBasedAppSources.InsertDirective(file.Content, $"#:package {name}@{directiveVersion}")));
            declaringFiles.Add(entryPoint);
        }

        foreach (var edit in edits)
        {
            edit.File.Write(_fileSystem, edit.Path, edit.Content);
        }

        return new DirectiveWrite(declaringFiles, [.. edits.Select(edit => edit.Path)], null);
    }

    /// <summary>
    /// The full paths of the files that hold a property-backed directive's version: each file the app compiles with a
    /// <c>#:property</c> line for the property (the SDK rejects the same property declared with different values, so
    /// each is changed together), or the Directory.* file that defines it. For an SDK named through a property with a
    /// literal version, the files with its directive.
    /// </summary>
    /// <param name="projectFilePath">The app's entry point.</param>
    public IReadOnlyList<string> VariableFiles(string projectFilePath, PackageVariableInfo variableInfo)
    {
        if (string.IsNullOrEmpty(variableInfo.VariableName))
        {
            return DeclaringFiles(projectFilePath, variableInfo.ReferenceKind, variableInfo.PackageName);
        }

        if (!variableInfo.FilePath.IsCSharpFile() || !projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [variableInfo.FilePath];
        }

        return PropertyFiles(ReadSources(projectFilePath), variableInfo);
    }

    // The files with a #:property line for the variable, or the file it was found in when none has one.
    private static List<string> PropertyFiles(FileBasedAppSources sources, PackageVariableInfo variableInfo)
    {
        var propertyRegex = PropertyDirectiveRegex(variableInfo.VariableName);
        var files = sources.Files
            .Where(file => file.Directives.Any(propertyRegex.IsMatch))
            .Select(file => file.Path)
            .ToList();
        return files.Count > 0 ? files : [variableInfo.FilePath];
    }

    /// <summary>
    /// Sets the property behind a property-backed directive (each <c>#:property</c> line for it, or a Directory.*
    /// property) to <paramref name="newVersion"/>, and makes the directive read it again. Nothing is written when an
    /// <c>#:include</c> could not be followed, as a file that wasn't read may declare the property too.
    /// </summary>
    /// <param name="projectFilePath">The app's entry point, whose files and imported properties are in effect.</param>
    /// <returns><see langword="true"/> when any file was changed.</returns>
    public bool TryUpdateVariable(string projectFilePath, PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        // A failure after the property was rewritten (say, evaluating the imported properties for the directive pass)
        // still reports the update that was written.
        var updated = false;
        try
        {
            UpdateVariable(projectFilePath, variableInfo, newVersion, ref updated);
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke(
                $"Failed to update package reference '{variableInfo.PackageName}' ({variableInfo.ElementType}): {ex.Message}");
        }

        return updated;
    }

    private void UpdateVariable(string projectFilePath, PackageVariableInfo variableInfo, NuGetVersion newVersion, ref bool updated)
    {
        var directiveReplaceRegex = variableInfo.ReferenceKind == ReferenceKind.Sdk
            ? SdkDirectiveReplaceRegex()
            : PackageDirectiveReplaceRegex();

        var isApp = projectFilePath.IsCSharpFile() && _fileSystem.File.Exists(projectFilePath);
        var sourcesBefore = isApp ? ReadSources(projectFilePath) : null;
        if (sourcesBefore != null && UnreadIncludes(sourcesBefore) is { } unread)
        {
            _onWarning?.Invoke($"Not updating '{variableInfo.PackageName}' in file-based app '{projectFilePath}': {unread}");
            return;
        }

        if (_fileSystem.File.Exists(variableInfo.FilePath) && !string.IsNullOrEmpty(variableInfo.VariableName))
        {
            if (variableInfo.FilePath.IsCSharpFile())
            {
                var propertyRegex = PropertyDirectiveRegex(variableInfo.VariableName);
                var propertyFiles = sourcesBefore != null ? PropertyFiles(sourcesBefore, variableInfo) : [variableInfo.FilePath];
                foreach (var propertyFilePath in propertyFiles)
                {
                    var propertyFile = EncodedText.Read(_fileSystem, propertyFilePath);
                    var propertyContent = propertyFile.Content;
                    var newPropertyContent = FileBasedAppSources.ReplaceInDirectives(
                        propertyContent,
                        propertyRegex,
                        match => match.Groups[1].Value + newVersion);
                    if (!string.Equals(propertyContent, newPropertyContent, StringComparison.Ordinal))
                    {
                        propertyFile.Write(_fileSystem, propertyFilePath, newPropertyContent);
                        updated = true;
                    }
                }
            }
            else if (TryUpdateXmlPropertyFile(projectFilePath, variableInfo.FilePath, variableInfo.VariableName, newVersion.ToString()))
            {
                updated = true;
            }
        }

        if (!isApp)
        {
            return;
        }

        // Read after the property was written, so a directive whose id is a property expression resolves with it. A
        // directive repeated in several files must keep the same text in each, so every file is rewritten.
        var sources = ReadSources(projectFilePath);
        var propertyDefinitions = sources.Properties;
        var directiveVersion = VersionExpression.ContainsPropertyReference(variableInfo.PackageReferenceVersion)
            ? variableInfo.PackageReferenceVersion
            : newVersion.ToString();

        foreach (var source in sources.Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            var content = file.Content;
            var newContent = FileBasedAppSources.ReplaceInDirectives(
                content,
                directiveReplaceRegex,
                match =>
                {
                    var directive = match.Groups[2].Value;
                    if (!TryParseDirective(directive, out var expression, out _) ||
                        (!string.Equals(expression, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                         (!FileBasedAppSources.TryResolveProperties(expression, propertyDefinitions, out var resolvedName) ||
                          !string.Equals(resolvedName, variableInfo.PackageName, StringComparison.OrdinalIgnoreCase))))
                    {
                        return match.Value;
                    }

                    return match.Groups[1].Value + variableInfo.PackageReferenceName + "@" + directiveVersion + match.Groups[3].Value;
                });

            if (!string.Equals(content, newContent, StringComparison.Ordinal))
            {
                file.Write(_fileSystem, source.Path, newContent);
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
        var sources = ReadSources(projectFilePath);
        var propertyDefinitions = sources.Properties;

        foreach (var source in sources.Files)
        {
            foreach (var line in source.Directives)
            {
                var packageMatch = PackageDirectiveLineRegex().Match(line);
                if (packageMatch.Success)
                {
                    ScanPackageLine(packageMatch.Groups[1].Value, source.Path, propertyDefinitions, packageVariables);
                    if (TryCreateReference(packageMatch.Groups[1].Value, source.Path, propertyDefinitions, ReferenceKind.Package, out var packageReference))
                    {
                        literalReferences.Add(packageReference);
                    }

                    continue;
                }

                var sdkMatch = SdkDirectiveLineRegex().Match(line);
                if (sdkMatch.Success)
                {
                    ScanSdkLine(sdkMatch.Groups[1].Value, source.Path, propertyDefinitions, sdkVariables);
                    if (TryCreateReference(sdkMatch.Groups[1].Value, source.Path, propertyDefinitions, ReferenceKind.Sdk, out var sdkReference))
                    {
                        literalReferences.Add(sdkReference);
                    }
                }
            }
        }

        return new ScanResult(packageVariables, sdkVariables, literalReferences);
    }

    private FileBasedAppSources ReadSources(string projectFilePath) =>
        FileBasedAppSources.Read(_fileSystem, _evaluator, projectFilePath);

    // Why the app's directives can't all be known, or null when they can.
    private static string? UnreadIncludes(FileBasedAppSources sources) =>
        sources.UnresolvedIncludes.Count == 0
            ? null
            : $"{string.Join(", ", sources.UnresolvedIncludes)} could not be followed, and the files it names may declare it too.";

    // Matches a #:property line for a property (case-insensitively): group 1 is the line up to its value, which runs to
    // the end of the line.
    private static Regex PropertyDirectiveRegex(string propertyName) =>
        new($@"(^[ \t]*#:[ \t]*property[ \t]+{Regex.Escape(propertyName)}[ \t]*=[ \t]*)[^\r\n]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Matches a directive for an id (case-insensitively): group 1 is the directive up to the id, group 2 the separator
    // (`@`, which whitespace may surround) and group 3 the version, when it has one. A directive whose separator has
    // no version after it matches without group 3.
    private static Regex DirectiveRegex(string directiveName, string name) =>
        new($@"(^[ \t]*#:[ \t]*{directiveName}[ \t]+{Regex.Escape(name)})(?:([ \t]*@[ \t]*)({VersionTokenPattern}))?(?=[\s@]|$)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // The id expression of a directive: what comes before its `@`, or its first token when it has no version.
    private static string NameExpression(string directive)
    {
        var text = directive.Trim();
        var separator = text.IndexOf('@', StringComparison.Ordinal);
        var name = (separator >= 0 ? text[..separator] : text).Trim();
        var whitespace = name.IndexOfAny([' ', '\t']);
        return whitespace < 0 ? name : name[..whitespace];
    }

    private static void ScanPackageLine(
        string directive,
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        Dictionary<string, PackageVariableInfo> result)
    {
        if (!TryParseDirective(directive, out var packageExpression, out var versionExpression) ||
            !FileBasedAppSources.TryResolveProperties(packageExpression, propertyDefinitions, out var packageName) ||
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
            !FileBasedAppSources.TryResolveProperties(sdkExpression, propertyDefinitions, out var sdkName) ||
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
        string filePath,
        Dictionary<string, (string Value, string FilePath)> propertyDefinitions,
        ReferenceKind kind,
        [NotNullWhen(true)] out FileBasedAppReference? reference)
    {
        reference = null;
        if (!TryParseDirective(directive, out var nameExpression, out var versionExpression) ||
            !FileBasedAppSources.TryResolveProperties(nameExpression, propertyDefinitions, out var name) ||
            !FileBasedAppSources.TryResolveProperties(versionExpression, propertyDefinitions, out var versionString) ||
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
            FilePath = filePath,
        };
        return true;
    }

    // Splits `Id@Version` (whitespace may surround the `@`) into the id and version expressions; the version is one
    // token, so a range holding a space doesn't parse.
    private static bool TryParseDirective(string directive, out string packageExpression, out string versionExpression)
    {
        packageExpression = string.Empty;
        versionExpression = string.Empty;

        var text = directive.Trim();
        var separatorIndex = text.IndexOf('@', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        packageExpression = text[..separatorIndex].Trim();
        var version = text[(separatorIndex + 1)..].TrimStart();
        var whitespaceIndex = version.IndexOfAny([' ', '\t']);
        versionExpression = whitespaceIndex < 0 ? version : version[..whitespaceIndex];
        return packageExpression.Length > 0 &&
               !packageExpression.Any(char.IsWhiteSpace) &&
               versionExpression.Length > 0;
    }
}

/// <summary>What writing a file-based app's <c>#:package</c> or <c>#:sdk</c> directives did.</summary>
/// <param name="DeclaringFiles">The files that declare the reference, including one a directive was added to.</param>
/// <param name="ChangedFiles">The files that were changed.</param>
/// <param name="Failure">Why nothing could be written; <see langword="null"/> when the directives were written.</param>
internal sealed record DirectiveWrite(IReadOnlyList<string> DeclaringFiles, IReadOnlyList<string> ChangedFiles, string? Failure)
{
    public static DirectiveWrite Failed(string failure) => new([], [], failure);
}
