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
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProjectEvaluator _evaluator = evaluator;
    private readonly Action<string>? _onWarning = onWarning;

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
    /// The ids of every package the app's <c>#:package</c> directives declare, whatever their version: those
    /// <see cref="References"/> leaves out, as their version is a range or there is none, too.
    /// </summary>
    public IReadOnlySet<string> DeclaredPackages(string projectFilePath)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return declared;
        }

        var sources = ReadSources(projectFilePath);
        foreach (var directive in sources.Files.SelectMany(file => file.Directives).Where(directive => directive.Kind == "package"))
        {
            if (FileBasedAppSources.TryResolveProperties(directive.Name, sources.Properties, out var name))
            {
                declared.Add(name);
            }
        }

        return declared;
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

        foreach (var directive in ReadSources(projectFilePath).Files.SelectMany(file => file.Directives))
        {
            if (directive.Kind == "package" && string.Equals(directive.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return VersionRange.TryParse(directive.Value ?? string.Empty, out var range) ? range : null;
            }
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

        var sources = ReadSources(projectFilePath);
        return [
            .. sources.Files
                .Where(file => file.Directives.Any(directive => Declares(directive, sources, kind, name)))
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

        var directiveKind = DirectiveKind(kind);
        foreach (var source in sources.Files)
        {
            // Rewriting the literal id below would miss this directive, and adding one would declare the id twice.
            if (source.Directives.FirstOrDefault(directive =>
                    VersionExpression.ContainsPropertyReference(directive.Name) && Declares(directive, sources, kind, name)) is { } named)
            {
                return DirectiveWrite.Failed(
                    $"{failure}: '{named.Line.Trim()}' in '{source.Path}' names it through a property; change the version by hand.");
            }
        }

        var edits = new List<(string Path, EncodedText File, string Content)>();
        var declaringFiles = new List<string>();
        foreach (var source in sources.Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            string? versionless = null;
            var declares = false;
            var content = FileBasedAppSources.RewriteDirectives(file.Content, directive =>
            {
                if (directive.Kind != directiveKind || !string.Equals(directive.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                declares = true;
                if (string.IsNullOrEmpty(directive.Value))
                {
                    versionless = directive.Line.Trim();
                    return null;
                }

                return string.Equals(directive.Value, versionText, StringComparison.Ordinal) ? null : directive.WithValue(versionText);
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
            edits.Add((entryPoint, file, FileBasedAppSources.InsertDirective(file.Content, $"#:package {FileBasedAppDirective.QuoteIfNeeded(name)}@{FileBasedAppDirective.QuoteIfNeeded(versionText)}")));
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
        var files = sources.Files
            .Where(file => file.Directives.Any(directive => DefinesProperty(directive, variableInfo.VariableName)))
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
                var propertyFiles = sourcesBefore != null ? PropertyFiles(sourcesBefore, variableInfo) : [variableInfo.FilePath];
                foreach (var propertyFilePath in propertyFiles)
                {
                    var propertyFile = EncodedText.Read(_fileSystem, propertyFilePath);
                    var propertyContent = propertyFile.Content;
                    var newPropertyContent = FileBasedAppSources.RewriteDirectives(
                        propertyContent,
                        directive => DefinesProperty(directive, variableInfo.VariableName) &&
                                     !string.Equals(directive.Value, newVersion.ToString(), StringComparison.Ordinal)
                            ? directive.WithValue(newVersion.ToString())
                            : null);
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

        var directiveKind = DirectiveKind(variableInfo.ReferenceKind);
        foreach (var source in sources.Files)
        {
            var file = EncodedText.Read(_fileSystem, source.Path);
            var content = file.Content;
            var newContent = FileBasedAppSources.RewriteDirectives(content, directive =>
            {
                if (directive.Kind != directiveKind ||
                    string.IsNullOrEmpty(directive.Value) ||
                    (!string.Equals(directive.Name, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                     !Declares(directive, sources, variableInfo.ReferenceKind, variableInfo.PackageName)))
                {
                    return null;
                }

                // Only what differs is rewritten, so the directive keeps its metadata, quoting and spacing.
                var name = string.Equals(directive.Name, variableInfo.PackageReferenceName, StringComparison.Ordinal)
                    ? directive.Name
                    : variableInfo.PackageReferenceName!;
                return name == directive.Name && directive.Value == directiveVersion
                    ? null
                    : directive.With(name, directiveVersion);
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
            foreach (var directive in source.Directives.Where(directive => directive.Kind is "package" or "sdk"))
            {
                var kind = directive.Kind == "sdk" ? ReferenceKind.Sdk : ReferenceKind.Package;
                if (kind == ReferenceKind.Sdk)
                {
                    ScanSdkLine(directive, source.Path, propertyDefinitions, sdkVariables);
                }
                else
                {
                    ScanPackageLine(directive, source.Path, propertyDefinitions, packageVariables);
                }

                if (TryCreateReference(directive, source.Path, propertyDefinitions, kind, out var reference))
                {
                    literalReferences.Add(reference);
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


    private static string DirectiveKind(ReferenceKind kind) => kind == ReferenceKind.Sdk ? "sdk" : "package";

    // Whether the directive declares the package or SDK: its id, with any properties resolved, is the name.
    private static bool Declares(FileBasedAppDirective directive, FileBasedAppSources sources, ReferenceKind kind, string name) =>
        directive.Kind == DirectiveKind(kind) &&
        FileBasedAppSources.TryResolveProperties(directive.Name, sources.Properties, out var resolved) &&
        string.Equals(resolved, name, StringComparison.OrdinalIgnoreCase);

    private static bool DefinesProperty(FileBasedAppDirective directive, string propertyName) =>
        directive.Kind == "property" && string.Equals(directive.Name, propertyName, StringComparison.OrdinalIgnoreCase);

    private static void ScanPackageLine(
        FileBasedAppDirective directive,
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
        FileBasedAppDirective directive,
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
        FileBasedAppDirective directive,
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

    // The id and version expressions of a #:package or #:sdk directive with a version.
    private static bool TryParseDirective(FileBasedAppDirective directive, out string packageExpression, out string versionExpression)
    {
        packageExpression = directive.Name;
        versionExpression = directive.Value ?? string.Empty;
        return versionExpression.Length > 0;
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
