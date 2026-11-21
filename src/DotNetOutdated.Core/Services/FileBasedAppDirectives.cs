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
    /// has no version, or names the id or version through a property), or when an <c>#:include</c> could not be
    /// followed (a file that wasn't read may declare it too). If writing one file fails, the files already written are
    /// changed back.
    /// </summary>
    public DirectiveWrite WriteDirective(string projectFilePath, ReferenceKind kind, string name, string versionText)
    {
        var failure = $"Failed to update file-based app '{projectFilePath}' for '{name}'";
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return DirectiveWrite.Failed($"{failure}: the file was not found.");
        }

        FileBasedAppSources sources;
        try
        {
            sources = ReadSources(projectFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DirectiveWrite.Failed($"{failure}: {ex.Message}");
        }

        if (UnreadIncludes(sources) is { } unread)
        {
            return DirectiveWrite.Failed($"{failure}: {unread}");
        }

        var directiveKind = DirectiveKind(kind);
        foreach (var source in sources.Files)
        {
            foreach (var directive in source.Directives.Where(directive => Declares(directive, sources, kind, name)))
            {
                // Rewriting the literal id below would miss this directive, and adding one would declare the id twice.
                if (FileBasedAppSources.HasExpression(directive.Name))
                {
                    return DirectiveWrite.Failed(
                        $"{failure}: '{directive.Line.Trim()}' in '{source.Path}' names it through a property; change the version by hand.");
                }

                // A literal written over it would stop the version following the property.
                if (FileBasedAppSources.HasExpression(directive.Value))
                {
                    return DirectiveWrite.Failed(
                        $"{failure}: '{directive.Line.Trim()}' in '{source.Path}' takes its version from a property that could not be updated; change the version by hand.");
                }
            }
        }

        var edits = new List<(string Path, EncodedText File, string Content)>();
        var declaringFiles = new List<string>();
        try
        {
            foreach (var source in sources.Files)
            {
                var file = EncodedText.Read(_fileSystem, source.Path);
                string? versionless = null;
                string? propertyVersion = null;
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

                    // The file may have changed since its directives were read, so its version is checked again in the
                    // text that is rewritten.
                    if (FileBasedAppSources.HasExpression(directive.Value))
                    {
                        propertyVersion = directive.Line.Trim();
                        return null;
                    }

                    return string.Equals(directive.Value, versionText, StringComparison.Ordinal) ? null : directive.WithValue(versionText);
                });

                if (versionless != null)
                {
                    return DirectiveWrite.Failed($"{failure}: '{versionless}' in '{source.Path}' has no version to update.");
                }

                if (propertyVersion != null)
                {
                    return DirectiveWrite.Failed(
                        $"{failure}: '{propertyVersion}' in '{source.Path}' takes its version from a property that could not be updated; change the version by hand.");
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DirectiveWrite.Failed($"{failure}: {ex.Message}");
        }

        return WriteAll(edits, failure) is { } writeFailure
            ? DirectiveWrite.Failed(writeFailure)
            : new DirectiveWrite(declaringFiles, [.. edits.Select(edit => edit.Path)], null);
    }

    // Writes each edit; if one fails, changes back the files written (the one that failed too, which a write can leave
    // part-written), as the SDK rejects a reference left at different versions. Returns why it failed, naming any file
    // that couldn't be changed back, or null.
    private string? WriteAll(List<(string Path, EncodedText File, string Content)> edits, string failure)
    {
        // Each file's bytes are kept as they were, so changing it back restores them exactly, even text that couldn't
        // be decoded.
        var written = new List<(string Path, byte[] Original)>();
        foreach (var edit in edits)
        {
            try
            {
                written.Add((edit.Path, _fileSystem.File.ReadAllBytes(edit.Path)));
                edit.File.Write(_fileSystem, edit.Path, edit.Content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var notRestored = new List<string>();
                foreach (var done in written)
                {
                    try
                    {
                        // The file that failed may not have been changed at all (it is read-only, say).
                        if (!_fileSystem.File.ReadAllBytes(done.Path).AsSpan().SequenceEqual(done.Original))
                        {
                            _fileSystem.File.WriteAllBytes(done.Path, done.Original);
                        }
                    }
                    catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                    {
                        notRestored.Add(done.Path);
                    }
                }

                var outcome = notRestored.Count == 0
                    ? "No file was changed."
                    : $"The version was already written in {string.Join(", ", notRestored.Select(path => $"'{path}'"))}, which could not be changed back; set it there to the version the other files declare.";
                return $"{failure}: '{edit.Path}' could not be written ({ex.Message}). {outcome}";
            }
        }

        return null;
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

        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [variableInfo.FilePath];
        }

        return PropertyFiles(ReadSources(projectFilePath), variableInfo);
    }

    // The app's files with a #:property line for the variable when it is defined in one of them (whatever its
    // extension, as the item mapping decides which files are compiled), or else the Directory.* file it was found in.
    private List<string> PropertyFiles(FileBasedAppSources sources, PackageVariableInfo variableInfo)
    {
        if (!IsSource(sources, variableInfo.FilePath))
        {
            return [variableInfo.FilePath];
        }

        var files = sources.Files
            .Where(file => file.Directives.Any(directive => DefinesProperty(directive, variableInfo.VariableName)))
            .Select(file => file.Path)
            .ToList();
        return files.Count > 0 ? files : [variableInfo.FilePath];
    }

    /// <summary>
    /// The files with a literal-version directive for the reference, whose version a property-backed upgrade writes too
    /// so that every declaration of it stays the same.
    /// </summary>
    public IReadOnlyList<string> LiteralDeclaringFiles(string projectFilePath, ReferenceKind kind, string name)
    {
        if (!projectFilePath.IsCSharpFile() || !_fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        var sources = ReadSources(projectFilePath);
        return [
            .. sources.Files
                .Where(file => file.Directives.Any(directive =>
                    Declares(directive, sources, kind, name) &&
                    !string.IsNullOrEmpty(directive.Value) &&
                    !FileBasedAppSources.HasExpression(directive.Value)))
                .Select(file => file.Path)
        ];
    }

    /// <summary>
    /// As <see cref="UpdateVariable"/>, reporting a failure as a warning.
    /// </summary>
    /// <returns><see langword="true"/> when any file was changed.</returns>
    public bool TryUpdateVariable(string projectFilePath, PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        var write = UpdateVariable(projectFilePath, variableInfo, newVersion);
        if (write.Failure != null)
        {
            _onWarning?.Invoke(write.Failure);
        }

        return write.ChangedFiles.Count > 0;
    }

    /// <summary>
    /// Sets the property behind a property-backed directive (each <c>#:property</c> line for it, or a Directory.*
    /// property) to <paramref name="newVersion"/>, and writes it in each literal-version directive for the reference
    /// too. Every file is written, or none: nothing is written when an <c>#:include</c> could not be followed (a file
    /// that wasn't read may declare the property too), when an include path uses the property, or when another
    /// directive for the reference takes its version from a different property; and if writing one file fails, the
    /// files already written are changed back.
    /// </summary>
    /// <param name="projectFilePath">The app's entry point, whose files and imported properties are in effect.</param>
    public VariableWrite UpdateVariable(string projectFilePath, PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        try
        {
            return UpdateVariableFiles(projectFilePath, variableInfo, newVersion);
        }
        catch (Exception ex)
        {
            return VariableWrite.Failed(
                $"Failed to update package reference '{variableInfo.PackageName}' ({variableInfo.ElementType}): {ex.Message}");
        }
    }

    private VariableWrite UpdateVariableFiles(string projectFilePath, PackageVariableInfo variableInfo, NuGetVersion newVersion)
    {
        var notUpdating = $"Not updating '{variableInfo.PackageName}' in file-based app '{projectFilePath}'";
        var isApp = projectFilePath.IsCSharpFile() && _fileSystem.File.Exists(projectFilePath);
        var sources = isApp ? ReadSources(projectFilePath) : null;
        if (sources != null && UnreadIncludes(sources) is { } unread)
        {
            return VariableWrite.Failed($"{notUpdating}: {unread}");
        }

        // Changing a property an include path uses can change the files the app compiles, which may declare it too.
        if (sources != null && !string.IsNullOrEmpty(variableInfo.VariableName) &&
            IncludeUsing(sources, variableInfo.VariableName) is { } include)
        {
            return VariableWrite.Failed(
                $"{notUpdating}: {include} uses $({variableInfo.VariableName}), " +
                "so changing it can change the files the app compiles; change the version by hand.");
        }

        // A declaration whose version is another property would be left at the old version.
        if (sources != null && OtherVersionProperty(sources, variableInfo) is { } other)
        {
            return VariableWrite.Failed(
                $"{notUpdating}: {other} takes its version from another property, which would be left at the old version; change the version by hand.");
        }

        // Each file's new content, in the order first changed.
        var edits = new List<(string Path, EncodedText File, string Content)>();
        void Edit(string path, Func<string, string> change)
        {
            var index = edits.FindIndex(edit => FileBasedAppSources.PathComparer.Equals(edit.Path, path));
            if (index < 0)
            {
                var file = EncodedText.Read(_fileSystem, path);
                edits.Add((path, file, change(file.Content)));
            }
            else
            {
                edits[index] = edits[index] with { Content = change(edits[index].Content) };
            }
        }

        var version = newVersion.ToString();
        if (_fileSystem.File.Exists(variableInfo.FilePath) && !string.IsNullOrEmpty(variableInfo.VariableName))
        {
            if (sources != null && IsSource(sources, variableInfo.FilePath))
            {
                foreach (var propertyFilePath in PropertyFiles(sources, variableInfo))
                {
                    Edit(propertyFilePath, content => FileBasedAppSources.RewriteDirectives(
                        content,
                        directive => DefinesProperty(directive, variableInfo.VariableName) &&
                                     !string.Equals(directive.Value, version, StringComparison.Ordinal)
                            ? directive.WithValue(version)
                            : null));
                }
            }
            else
            {
                var failure = XmlPropertyEdit(projectFilePath, variableInfo.FilePath, variableInfo.VariableName, version, Edit);
                if (failure != null)
                {
                    return VariableWrite.Failed(failure);
                }
            }
        }

        if (sources != null)
        {
            // A directive repeated in several files must keep the same version in each, so every file is rewritten:
            // one that reads the property sees the new value, and a literal version is written. Each directive keeps
            // its own id and version expression, as other apps that include its file may not define the properties
            // this app's directive uses.
            var directiveKind = DirectiveKind(variableInfo.ReferenceKind);
            foreach (var source in sources.Files)
            {
                Edit(source.Path, content => FileBasedAppSources.RewriteDirectives(content, directive =>
                {
                    if (directive.Kind != directiveKind ||
                        string.IsNullOrEmpty(directive.Value) ||
                        FileBasedAppSources.HasExpression(directive.Value) ||
                        (!string.Equals(directive.Name, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) &&
                         !Declares(directive, sources, variableInfo.ReferenceKind, variableInfo.PackageName)))
                    {
                        return null;
                    }

                    // Only the version is rewritten, so the directive keeps its metadata, quoting and spacing.
                    return directive.Value == version ? null : directive.WithValue(version);
                }));
            }
        }

        var changed = edits.Where(edit => !string.Equals(edit.Content, edit.File.Content, StringComparison.Ordinal)).ToList();
        return WriteAll(changed, $"Failed to update '{variableInfo.PackageName}' in file-based app '{projectFilePath}'") is { } writeFailure
            ? VariableWrite.Failed(writeFailure)
            : new VariableWrite([.. changed.Select(edit => edit.Path)], null);
    }

    // The first directive for the reference whose version uses other properties than the variable's directive does.
    private static string? OtherVersionProperty(FileBasedAppSources sources, PackageVariableInfo variableInfo)
    {
        static HashSet<string> PropertyNames(string? expression) =>
            VersionExpression.PropertyReferenceRegex().Matches(expression ?? string.Empty)
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var own = PropertyNames(variableInfo.PackageReferenceVersion);
        var directiveKind = DirectiveKind(variableInfo.ReferenceKind);
        return sources.Files
            .SelectMany(file => file.Directives
                .Where(directive =>
                    directive.Kind == directiveKind &&
                    FileBasedAppSources.HasExpression(directive.Value) &&
                    (string.Equals(directive.Name, variableInfo.PackageReferenceName, StringComparison.OrdinalIgnoreCase) ||
                     Declares(directive, sources, variableInfo.ReferenceKind, variableInfo.PackageName)) &&
                    !PropertyNames(directive.Value).SetEquals(own))
                .Select(directive => $"'{directive.Line.Trim()}' in '{file.Path}'"))
            .FirstOrDefault();
    }

    // Edits the definition of variableName that is in effect for the file-based app at projectFilePath; returns why
    // it can't, or null.
    private string? XmlPropertyEdit(
        string projectFilePath,
        string propertyFilePath,
        string variableName,
        string newMSBuildValue,
        Action<string, Func<string, string>> edit)
    {
        var properties = _evaluator.GetImportedProperties(projectFilePath);
        if (!properties.TryGetValue(variableName, out var definition) ||
            definition.Ambiguous ||
            definition.ElementIndex < 0 ||
            !string.Equals(definition.FilePath, _fileSystem.Path.GetFullPath(propertyFilePath), StringComparison.OrdinalIgnoreCase))
        {
            return $"Not updating $({variableName}) in '{propertyFilePath}': the definition in effect could not be determined.";
        }

        edit(propertyFilePath, content => PackageVersionMarkup.ReplacePropertyValueAt(content, variableName, definition.ElementIndex, newMSBuildValue));
        return null;
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

    // The first include or exclude directive whose path (or the item mapping it is matched against) uses the property,
    // directly or through other properties, or null.
    private static string? IncludeUsing(FileBasedAppSources sources, string propertyName)
    {
        var dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { propertyName };
        bool UsesDependent(string expression) =>
            VersionExpression.PropertyReferenceRegex().Matches(expression).Any(match => dependents.Contains(match.Groups[1].Value));

        bool added;
        do
        {
            added = false;
            foreach (var (name, definition) in sources.Properties)
            {
                if (!dependents.Contains(name) && UsesDependent(definition.Value))
                {
                    added |= dependents.Add(name);
                }
            }
        }
        while (added);

        var mappingUses = dependents.Contains("FileBasedProgramsItemMapping");
        return sources.Files
            .SelectMany(file => file.Directives
                .Where(directive => directive.Kind is "include" or "exclude" && (mappingUses || UsesDependent(directive.Name)))
                .Select(directive => $"'{directive.Line.Trim()}' in '{file.Path}'"))
            .FirstOrDefault();
    }

    // Why the app's directives can't all be known, or null when they can.
    private static string? UnreadIncludes(FileBasedAppSources sources) =>
        sources.UnresolvedIncludes.Count == 0
            ? null
            : $"{string.Join(", ", sources.UnresolvedIncludes)} could not be followed, and the files it names may declare it too.";


    // Whether the file is one of the app's sources, whose directives define its properties.
    private bool IsSource(FileBasedAppSources sources, string filePath)
    {
        var fullPath = _fileSystem.Path.GetFullPath(filePath);
        return sources.Files.Any(file => FileBasedAppSources.PathComparer.Equals(file.Path, fullPath));
    }

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

/// <summary>What writing a file-based app's property-backed version did.</summary>
/// <param name="ChangedFiles">The files that were changed.</param>
/// <param name="Failure">Why nothing could be written; <see langword="null"/> when the version was written.</param>
internal sealed record VariableWrite(IReadOnlyList<string> ChangedFiles, string? Failure)
{
    public static VariableWrite Failed(string failure) => new([], failure);
}
