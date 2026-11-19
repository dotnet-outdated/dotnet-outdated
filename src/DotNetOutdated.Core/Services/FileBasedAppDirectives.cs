using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
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
    // Included files can include others, and an exclude can drop a file that included more, so the set of files is
    // read again until it settles; a set that keeps changing is cut off here.
    private const int MaxIncludePasses = 32;

    // A version after `@`: quoted (as `dotnet package add` writes a range holding a space), a range that may hold a
    // space, or a bare token.
    private const string VersionTokenPattern = @"(?:""(?:[^""\\\r\n]|\\.)*""|[\[\(][^\]\)\r\n]*[\]\)]|[^\s]+)";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProjectEvaluator _evaluator = evaluator;
    private readonly Action<string>? _onWarning = onWarning;

    [GeneratedRegex(@"^[ \t]*#:[ \t]*(include|exclude)[ \t]+(.+?)\s*$")]
    private static partial Regex IncludeDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#[:!]")]
    private static partial Regex DirectiveOrShebangLineRegex();

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

        foreach (var line in ReadSources(projectFilePath).Files.SelectMany(file => file.Lines))
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
    /// Rewrites the version of every literal <c>#:package Id@Version</c> or <c>#:sdk Id@Version</c> directive for
    /// <paramref name="name"/> in the files the app compiles. The SDK rejects the same id declared with different
    /// versions, so a directive repeated in several files is changed in each.
    /// </summary>
    /// <returns><see langword="true"/> when a directive line was changed.</returns>
    public bool TryUpdateDirectReference(string projectFilePath, string name, ReferenceKind kind, NuGetVersion newVersion)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return false;
        }

        var directiveRegex = DirectiveRegex(kind == ReferenceKind.Sdk ? "sdk" : "package", name);
        var updated = false;
        foreach (var source in ReadSources(projectFilePath).Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            var content = file.Content;
            var updatedContent = directiveRegex.Replace(
                content,
                match => match.Groups[2].Success ? $"{match.Groups[1].Value}@{newVersion}" : match.Value);

            if (!string.Equals(content, updatedContent, StringComparison.Ordinal))
            {
                file.Write(_fileSystem, source.Path, updatedContent);
                updated = true;
            }
        }

        return updated;
    }

    /// <summary>
    /// Writes <paramref name="versionText"/> to every <c>#:package</c> directive for <paramref name="name"/> in the
    /// files the app compiles, or, when none declares it, adds a directive to the entry point. Nothing is written when
    /// a directive for it has no version to change, or when no directive was found but an <c>#:include</c> could not
    /// be followed (the package may be declared in a file that wasn't read).
    /// </summary>
    public PackageDirectiveWrite WritePackageDirective(string projectFilePath, string name, string versionText)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return new PackageDirectiveWrite(false, projectFilePath, $"File-based app '{projectFilePath}' was not found.");
        }

        var sources = ReadSources(projectFilePath);
        var directiveRegex = DirectiveRegex("package", name);
        var directiveVersion = versionText.Any(char.IsWhiteSpace) ? $"\"{versionText}\"" : versionText;
        var edits = new List<(string Path, EncodedText File, string Content)>();
        string? declaringFile = null;
        foreach (var source in sources.Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            string? versionless = null;
            var content = directiveRegex.Replace(file.Content, match =>
            {
                if (!match.Groups[2].Success)
                {
                    versionless = match.Value.Trim();
                    return match.Value;
                }

                return $"{match.Groups[1].Value}@{directiveVersion}";
            });

            if (versionless != null)
            {
                return new PackageDirectiveWrite(
                    false,
                    source.Path,
                    $"Failed to update file-based app '{projectFilePath}' for '{name}': '{versionless}' in '{source.Path}' " +
                    "has no version to update.");
            }

            if (directiveRegex.IsMatch(file.Content))
            {
                declaringFile ??= source.Path;
            }

            if (!string.Equals(content, file.Content, StringComparison.Ordinal))
            {
                edits.Add((source.Path, file, content));
            }
        }

        if (declaringFile == null)
        {
            if (sources.UnresolvedIncludes.Count > 0)
            {
                return new PackageDirectiveWrite(
                    false,
                    projectFilePath,
                    $"Failed to update file-based app '{projectFilePath}' for '{name}': no #:package directive for it was " +
                    $"found, and {string.Join(", ", sources.UnresolvedIncludes)} could not be followed to look for one.");
            }

            declaringFile = sources.Files[0].Path;
            var entryPoint = EncodedText.Read(_fileSystem, declaringFile);
            edits.Add((declaringFile, entryPoint, InsertDirective(entryPoint.Content, $"#:package {name}@{directiveVersion}")));
        }

        foreach (var edit in edits)
        {
            edit.File.Write(_fileSystem, edit.Path, edit.Content);
        }

        return new PackageDirectiveWrite(edits.Count > 0, declaringFile, null);
    }

    /// <summary>
    /// Sets the property behind a property-backed directive (a <c>#:property</c> line or a Directory.* property) to
    /// <paramref name="newVersion"/>, and makes the directive read it again.
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
            else if (TryUpdateXmlPropertyFile(projectFilePath, variableInfo.FilePath, variableInfo.VariableName, newVersion.ToString()))
            {
                updated = true;
            }
        }

        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
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
            foreach (var line in source.Lines)
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

    private sealed record SourceFile(string Path, IReadOnlyList<string> Lines);

    /// <param name="Files">The entry point, then the included files.</param>
    /// <param name="Properties">The imported properties, overridden by the files' <c>#:property</c> lines.</param>
    /// <param name="HasIncludes">Whether any of the files has an <c>#:include</c> directive.</param>
    /// <param name="UnresolvedIncludes">The include directives whose path uses a property that isn't defined.</param>
    private sealed record Sources(
        IReadOnlyList<SourceFile> Files,
        Dictionary<string, (string Value, string FilePath)> Properties,
        bool HasIncludes,
        IReadOnlyList<string> UnresolvedIncludes);

    // The SDK reads directives from every file the app compiles, so properties and includes from any of them apply to
    // all. The entry point is always compiled (its Compile item is added whatever #:exclude says).
    private Sources ReadSources(string projectFilePath)
    {
        var entryPoint = new SourceFile(_fileSystem.Path.GetFullPath(projectFilePath), [.. _fileSystem.File.ReadLines(projectFilePath)]);
        var importedProperties = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
        CollectEvaluatedProperties(projectFilePath, importedProperties);

        var files = new List<SourceFile> { entryPoint };
        for (var pass = 0; ; pass++)
        {
            var properties = new Dictionary<string, (string Value, string FilePath)>(importedProperties, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                foreach (var line in file.Lines)
                {
                    CollectPropertyFromLine(line, file.Path, properties);
                }
            }

            var includes = FollowIncludes(files, properties);
            var included = includes.Files.Where(path => !string.Equals(path, entryPoint.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pass == MaxIncludePasses ||
                included.SequenceEqual(files.Skip(1).Select(file => file.Path), StringComparer.OrdinalIgnoreCase))
            {
                return new Sources(files, properties, includes.HasIncludes, includes.Unresolved);
            }

            var read = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            files = [entryPoint, .. included.Select(path => read.TryGetValue(path, out var file) ? file : new SourceFile(path, [.. _fileSystem.File.ReadLines(path)]))];
        }
    }

    // The .cs files the #:include directives of these files add, less those their #:exclude directives remove.
    private (List<string> Files, bool HasIncludes, List<string> Unresolved) FollowIncludes(
        List<SourceFile> files,
        Dictionary<string, (string Value, string FilePath)> properties)
    {
        var included = new List<string>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        var hasIncludes = false;
        foreach (var file in files)
        {
            var directory = _fileSystem.Path.GetDirectoryName(file.Path)!;
            foreach (var line in file.Lines)
            {
                var match = IncludeDirectiveLineRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var isInclude = match.Groups[1].Value == "include";
                hasIncludes |= isInclude;
                if (!TryParsePathValue(match.Groups[2].Value, out var pattern))
                {
                    continue;
                }

                if (!TryResolveProperties(pattern, properties, out var resolvedPattern))
                {
                    unresolved.Add($"'#:{match.Groups[1].Value} {pattern}' in '{file.Path}'");
                    continue;
                }

                var matches = MatchFiles(resolvedPattern, directory);
                if (isInclude)
                {
                    included.AddRange(matches);
                }
                else
                {
                    excluded.UnionWith(matches);
                }
            }
        }

        return ([.. included.Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !excluded.Contains(path))], hasIncludes, unresolved);
    }

    // The path an #:include or #:exclude names: its first token, or a whole quoted value with its escapes decoded.
    // Trailing Name=Value metadata is ignored.
    private static bool TryParsePathValue(string value, out string path)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var builder = new StringBuilder();
            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] == '"')
                {
                    path = builder.ToString();
                    return path.Length > 0;
                }

                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    i++;
                }

                builder.Append(value[i]);
            }

            path = string.Empty;
            return false;
        }

        var end = value.IndexOfAny([' ', '\t']);
        path = end < 0 ? value : value[..end];
        return path.Length > 0;
    }

    // The .cs files a path or glob (*, ? and **, as MSBuild matches them) names, relative to baseDirectory.
    private List<string> MatchFiles(string pattern, string baseDirectory)
    {
        var path = _fileSystem.Path.Combine(baseDirectory, pattern).Replace('\\', '/');
        var wildcard = path.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            var fullPath = _fileSystem.Path.GetFullPath(path);
            return fullPath.IsCSharpFile() && _fileSystem.File.Exists(fullPath) ? [fullPath] : [];
        }

        var rootEnd = path.LastIndexOf('/', wildcard);
        var root = _fileSystem.Path.GetFullPath(rootEnd < 0 ? baseDirectory : path[..(rootEnd + 1)]);
        var glob = path[(rootEnd + 1)..];
        if (!_fileSystem.Directory.Exists(root))
        {
            return [];
        }

        var globRegex = GlobRegex(glob);
        var searchOption = glob.Contains('/') || glob.Contains("**", StringComparison.Ordinal)
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            return [
                .. _fileSystem.Directory.EnumerateFiles(root, "*", searchOption)
                    .Where(file => file.IsCSharpFile() && globRegex.IsMatch(_fileSystem.Path.GetRelativePath(root, file).Replace('\\', '/')))
                    .Select(file => _fileSystem.Path.GetFullPath(file))
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory under the glob that can't be read; the build would report it.
            return [];
        }
    }

    private static Regex GlobRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '/':
                    // `**/` matches any number of directories, including none.
                    builder.Append("(?:.*/)?");
                    i += 2;
                    break;
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    builder.Append(".*");
                    i++;
                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(glob[i].ToString()));
                    break;
            }
        }

        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant;
        return new Regex(builder.Append('$').ToString(), options);
    }

    // Matches a directive for an id (case-insensitively): group 1 is the directive up to the id, and group 2 its
    // version, when it has one.
    private static Regex DirectiveRegex(string directiveName, string name) =>
        new($@"(^[ \t]*#:[ \t]*{directiveName}[ \t]+{Regex.Escape(name)})(?:@({VersionTokenPattern}))?(?=\s|$)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Adds a directive line after the last directive (or shebang) at the top of the file, or as its first line.
    private static string InsertDirective(string content, string directive)
    {
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertAt = 0;
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var next = lineEnd < 0 ? content.Length : lineEnd + 1;
            var line = content[lineStart..next].TrimEnd('\r', '\n');
            if (DirectiveOrShebangLineRegex().IsMatch(line))
            {
                insertAt = next;
            }
            else if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                // Directives can't follow the first C# token.
                break;
            }

            lineStart = next;
        }

        var prefix = content[..insertAt];
        if (prefix.Length > 0 && !prefix.EndsWith('\n'))
        {
            prefix += newLine;
        }

        return prefix + directive + newLine + content[insertAt..];
    }

    // Properties visible to a file-based app from Directory.Build.props and friends (raw values, for chain resolution).
    private void CollectEvaluatedProperties(string projectFilePath, Dictionary<string, (string Value, string FilePath)> propertyDefinitions)
    {
        foreach (var (name, definition) in _evaluator.GetImportedProperties(projectFilePath))
        {
            propertyDefinitions[name] = (definition.RawValue, definition.FilePath);
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
        string filePath,
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
            FilePath = filePath,
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

/// <summary>What writing a file-based app's <c>#:package</c> directive did.</summary>
/// <param name="Changed">Whether a file was changed.</param>
/// <param name="FilePath">The first file that declares the package, or the one it was added to.</param>
/// <param name="Failure">Why nothing could be written; <see langword="null"/> when the directive was written.</param>
internal sealed record PackageDirectiveWrite(bool Changed, string FilePath, string? Failure);
