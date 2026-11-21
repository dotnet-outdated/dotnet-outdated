using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services;

/// <summary>A C# file a file-based app compiles, and the file-level directives it declares.</summary>
internal sealed record FileBasedAppSourceFile(string Path, IReadOnlyList<FileBasedAppDirective> Directives);

/// <summary>
/// The C# files a .NET file-based app compiles and the properties its directives see, found the way the SDK finds
/// them: its entry point, then each file the <c>#:include</c> and <c>#:exclude</c> directives of the files read so far
/// leave as a Compile item, read in turn. Only a file's leading directives count, as for the SDK: those before its
/// first C# token or <c>#if</c>, outside comments.
/// </summary>
/// <param name="Files">The entry point, then the included files in the order they were found.</param>
/// <param name="Properties">The imported properties, overridden by the files' <c>#:property</c> lines.</param>
/// <param name="HasIncludes">Whether any of the files has an <c>#:include</c> directive.</param>
/// <param name="UnresolvedIncludes">
/// The include and exclude directives whose path (or the item mapping it is matched against) uses a property that
/// isn't defined, or whose imported value isn't certain: the files they name weren't read.
/// </param>
internal sealed partial record FileBasedAppSources(
    IReadOnlyList<FileBasedAppSourceFile> Files,
    Dictionary<string, (string Value, string FilePath)> Properties,
    bool HasIncludes,
    IReadOnlyList<string> UnresolvedIncludes)
{
    /// <summary>Compares file paths as the SDK does when it tracks the files it has read.</summary>
    public static readonly StringComparer PathComparer = StringComparer.Ordinal;

    // The property that maps an included file's extension to its item type, and its value in a file-based app's
    // project unless Directory.Build.props or a #:property line sets it.
    private const string ItemMappingProperty = "FileBasedProgramsItemMapping";
    private const string DefaultItemMapping = ".cs=Compile;.resx=EmbeddedResource;.json=None;.razor=Content;.dll=Reference";

    [GeneratedRegex(@"^#[ \t]*if\b")]
    private static partial Regex IfDirectiveRegex();

    public static FileBasedAppSources Read(IFileSystem fileSystem, IProjectEvaluator evaluator, string projectFilePath)
    {
        var entryPoint = fileSystem.Path.GetFullPath(projectFilePath);
        var imported = evaluator.GetImportedProperties(projectFilePath);

        // Versions are edited where they are written, so a property keeps its unexpanded value. Include paths are
        // expanded as MSBuild expands them in the app's project, so they see a property's value as it was evaluated
        // when assigned (an imported one can use $(MSBuildThisFileDirectory), say) and the project's reserved
        // properties; a property whose value isn't certain is left undefined for them.
        var properties = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
        var pathProperties = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase)
        {
            [ItemMappingProperty] = (DefaultItemMapping, entryPoint),
        };

        // Directory.Build.targets is imported after the app's #:property lines, so its definitions are the final
        // values, which items (and so include paths) see. A #:property line still assigns its own value first, which a
        // later #:property line expanding it takes. The final value was evaluated without the app's #:property lines,
        // so for paths it is only certain when it uses no other property.
        var reserved = ReservedProperties(fileSystem, entryPoint).ToList();
        var finalPathValues = new Dictionary<string, (string Value, string FilePath)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in imported)
        {
            properties[name] = (definition.RawValue, definition.FilePath);
            pathProperties.Remove(name);
            var certain = definition is { Ambiguous: false, Value: not null };
            if (!definition.AfterProject)
            {
                if (certain)
                {
                    pathProperties[name] = (definition.Value!, definition.FilePath);
                }
            }
            else
            {
                // Its value before Directory.Build.targets assigns it isn't known, so it is undefined until a #:property
                // line assigns one.
                finalPathValues[name] = certain && !UsesOtherProperties(definition.RawValue, reserved)
                    ? (definition.Value!, definition.FilePath)
                    : null;
            }
        }

        foreach (var (name, value) in reserved)
        {
            pathProperties[name] = (value, entryPoint);
        }

        // The values an include path sees: the final ones.
        Dictionary<string, (string Value, string FilePath)> ItemProperties()
        {
            var itemProperties = new Dictionary<string, (string Value, string FilePath)>(pathProperties, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in finalPathValues)
            {
                if (value is { } final)
                {
                    itemProperties[name] = final;
                }
                else
                {
                    itemProperties.Remove(name);
                }
            }

            return itemProperties;
        }

        var files = new List<FileBasedAppSourceFile>();
        var seen = new HashSet<string>(PathComparer) { entryPoint };
        var toRead = new Queue<string>([entryPoint]);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Directory.Build.targets definitions whose condition took a property to be undefined (the default idiom
        // Condition="'$(X)' == ''") that a #:property line read so far defines: they may not apply, so they are
        // uncertain from that line on, for include paths read after it, and the app's own value is kept for versions.
        var uncertain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The Compile items the include and exclude directives read so far leave, in order: the SDK writes them as
        // ordered Include and Remove items, so a later include can add back what an earlier exclude removed. A file
        // once read keeps its directives, as for the SDK, even if a later exclude removes it.
        var compileItems = new List<string>();
        var hasIncludes = false;
        var unresolved = new List<string>();
        while (toRead.TryDequeue(out var path))
        {
            var file = new FileBasedAppSourceFile(path, ParseDirectives(fileSystem.File.ReadAllText(path)));
            files.Add(file);
            foreach (var directive in file.Directives.Where(directive => directive.Kind == "property"))
            {
                // A later #:property line overrides an earlier one, as in MSBuild, and a Directory.Build.targets
                // definition overrides both (unless its condition took the property to be undefined). MSBuild expands
                // a property's value when it is assigned, so for paths it takes the values in effect then; one using a
                // property that isn't defined (or isn't certain) is left undefined for them.
                assigned.Add(directive.Name);
                foreach (var (name, _) in imported.Where(pair =>
                    pair.Value.AfterProject && (pair.Value.AssumedUndefined ?? []).Contains(directive.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    uncertain.Add(name);
                    finalPathValues[name] = null;
                }

                if (!imported.TryGetValue(directive.Name, out var definition) || !definition.AfterProject || uncertain.Contains(directive.Name))
                {
                    properties[directive.Name] = (directive.Value!, path);
                }

                if (TryResolveProperties(directive.Value!, pathProperties, out var pathValue))
                {
                    pathProperties[directive.Name] = (pathValue, path);
                }
                else
                {
                    pathProperties.Remove(directive.Name);
                }
            }

            var itemProperties = ItemProperties();
            var directory = fileSystem.Path.GetDirectoryName(path)!;
            foreach (var directive in file.Directives.Where(directive => directive.Kind is "include" or "exclude"))
            {
                var isInclude = directive.Kind == "include";
                hasIncludes |= isInclude;
                if (!TryResolveProperties(directive.Name, itemProperties, out var resolvedPatterns))
                {
                    unresolved.Add($"'{directive.Line.Trim()}' in '{path}'");
                    continue;
                }

                if (!itemProperties.TryGetValue(ItemMappingProperty, out var mappingDefinition) ||
                    !TryResolveProperties(mappingDefinition.Value, itemProperties, out var mapping))
                {
                    unresolved.Add($"'{directive.Line.Trim()}' in '{path}' (its item type, from $({ItemMappingProperty}))");
                    continue;
                }

                // Only the files the mapping makes Compile items are compiled, and so declare directives; a path whose
                // extension the mapping doesn't name is an error the build reports. The SDK gives the whole directive
                // one item type, from the end of its value, which MSBuild then splits on ';' into entries of their own.
                if (!string.Equals(ItemType(mapping, resolvedPatterns), "Compile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var resolvedPattern in resolvedPatterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pattern = fileSystem.Path.Combine(directory, resolvedPattern);
                    if (MatchFiles(fileSystem, pattern) is not { } matches)
                    {
                        unresolved.Add($"'{directive.Line.Trim()}' in '{path}' (a directory it searches could not be read)");
                        continue;
                    }

                    if (isInclude)
                    {
                        compileItems.AddRange(matches);
                    }
                    else
                    {
                        var removed = matches.ToHashSet(PathComparer);
                        compileItems.RemoveAll(removed.Contains);
                    }
                }
            }

            foreach (var item in compileItems.Where(seen.Add))
            {
                toRead.Enqueue(item);
            }
        }

        // An imported property that expands one a #:property line redefines took that property's imported value when
        // it was assigned, before the app's lines; which definition holds its version isn't known, so it isn't
        // followed for versions.
        foreach (var (name, definition) in imported)
        {
            if (!definition.AfterProject && !assigned.Contains(name) &&
                VersionExpression.PropertyReferenceRegex().Matches(definition.RawValue).Any(match => assigned.Contains(match.Groups[1].Value)))
            {
                properties.Remove(name);
            }
        }

        return new FileBasedAppSources(files, properties, hasIncludes, unresolved);
    }

    // Whether an imported value uses a property other than the project's reserved ones, or any other expression.
    private static bool UsesOtherProperties(string rawValue, List<(string Name, string Value)> reserved)
    {
        var withoutReserved = VersionExpression.PropertyReferenceRegex().Replace(
            rawValue,
            match => reserved.Any(property => string.Equals(property.Name, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                ? string.Empty
                : match.Value);
        return HasExpression(withoutReserved);
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds an MSBuild expression: a property reference or function, or an item or
    /// metadata reference.
    /// </summary>
    public static bool HasExpression(string? text) =>
        text != null &&
        (text.Contains("$(", StringComparison.Ordinal) ||
         text.Contains("@(", StringComparison.Ordinal) ||
         text.Contains("%(", StringComparison.Ordinal));

    // The item type the mapping (`.ext=ItemType;...`, its first entry for the path's extension) gives a path.
    private static string? ItemType(string mapping, string path)
    {
        foreach (var entry in mapping.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=');
            if (parts.Length == 2 &&
                parts[0].Trim() is [ '.', _, .. ] extension &&
                parts[1].Trim() is { Length: > 0 } itemType &&
                path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return itemType;
            }
        }

        return null;
    }

    // The reserved properties of a file-based app's project, whose path is the entry point's with .csproj appended.
    private static IEnumerable<(string Name, string Value)> ReservedProperties(IFileSystem fileSystem, string entryPoint)
    {
        var projectPath = entryPoint + ".csproj";
        var directory = fileSystem.Path.GetDirectoryName(projectPath) ?? string.Empty;
        yield return ("MSBuildProjectDirectory", directory.TrimEnd('\\', '/'));
        yield return ("MSBuildProjectFullPath", projectPath);
        yield return ("MSBuildProjectFile", fileSystem.Path.GetFileName(projectPath));
        yield return ("MSBuildProjectName", fileSystem.Path.GetFileNameWithoutExtension(projectPath));
        yield return ("MSBuildProjectExtension", ".csproj");
        yield return ("MSBuildThisFileDirectory", directory.TrimEnd('\\', '/') + fileSystem.Path.DirectorySeparatorChar);
        yield return ("MSBuildThisFileFullPath", projectPath);
        yield return ("MSBuildThisFile", fileSystem.Path.GetFileName(projectPath));
        yield return ("MSBuildThisFileName", fileSystem.Path.GetFileNameWithoutExtension(projectPath));
    }

    /// <summary>
    /// Rewrites the file-level directives of <paramref name="content"/>: each one <paramref name="rewrite"/> returns a
    /// line for is replaced by it. Text in comments and code is left alone.
    /// </summary>
    public static string RewriteDirectives(string content, Func<FileBasedAppDirective, string?> rewrite)
    {
        var builder = new StringBuilder(content.Length);
        var copied = 0;
        foreach (var (start, end) in DirectiveLineSpans(content))
        {
            if (FileBasedAppDirective.TryParse(content[start..end], out var directive) && rewrite(directive) is { } line)
            {
                builder.Append(content, copied, start - copied).Append(line);
                copied = end;
            }
        }

        return builder.Append(content, copied, content.Length - copied).ToString();
    }

    /// <summary>Adds a directive line after the file's last directive (or shebang), or as its first line.</summary>
    public static string InsertDirective(string content, string directive)
    {
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : content.Contains('\r') && !content.Contains('\n') ? "\r"
            : "\n";
        var spans = DirectiveLineSpans(content);
        if (spans.Count == 0)
        {
            return directive + newLine + content;
        }

        // The new line ends as the line it follows does.
        var nextLine = NextLineStart(content, spans[^1].End);
        if (nextLine > spans[^1].End)
        {
            newLine = content[spans[^1].End..nextLine];
        }

        return nextLine == spans[^1].End
            ? content + newLine + directive + newLine
            : content[..nextLine] + directive + newLine + content[nextLine..];
    }

    // The characters C# ends a line at; \r\n is one line break.
    private static readonly char[] LineBreaks = ['\r', '\n', '\u0085', '\u2028', '\u2029'];

    // The index after the line break at lineEnd (where a line's text ends), or lineEnd at the end of the content.
    private static int NextLineStart(string content, int lineEnd) =>
        lineEnd >= content.Length ? lineEnd
        : content[lineEnd] == '\r' && lineEnd + 1 < content.Length && content[lineEnd + 1] == '\n' ? lineEnd + 2
        : lineEnd + 1;

    /// <summary>
    /// The value of <paramref name="expression"/> with its <c>$(Name)</c> references expanded; <see langword="false"/>
    /// when one isn't defined, the expansion doesn't finish, or it leaves an expression this can't evaluate (a property
    /// function such as <c>$([System.String]::Copy('a'))</c>, or an item or metadata reference).
    /// </summary>
    public static bool TryResolveProperties(string expression, Dictionary<string, (string Value, string FilePath)> propertyDefinitions, out string resolvedValue)
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
                return !HasExpression(current);
            }
        }

        resolvedValue = current;
        return !HasExpression(current);
    }

    private static List<FileBasedAppDirective> ParseDirectives(string content) =>
    [
        .. DirectiveLineSpans(content)
            .Select(span => FileBasedAppDirective.TryParse(content[span.Start..span.End], out var directive) ? directive : null)
            .OfType<FileBasedAppDirective>()
    ];

    // The start and end (before its line break) of each #: or #! line before the file's first C# token or #if,
    // outside comments: the lines the SDK reads directives from.
    private static List<(int Start, int End)> DirectiveLineSpans(string content)
    {
        var spans = new List<(int Start, int End)>();
        var inComment = false;
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineBreak = content.IndexOfAny(LineBreaks, lineStart);
            var textEnd = lineBreak < 0 ? content.Length : lineBreak;
            var line = content[lineStart..textEnd];
            if (!inComment && line.TrimStart().StartsWith('#'))
            {
                var directive = line.TrimStart();
                if (directive.StartsWith("#:", StringComparison.Ordinal) || directive.StartsWith("#!", StringComparison.Ordinal))
                {
                    spans.Add((lineStart, textEnd));
                }
                else if (IfDirectiveRegex().IsMatch(directive))
                {
                    break;
                }
            }
            else if (!IsTrivia(line, ref inComment))
            {
                break;
            }

            lineStart = NextLineStart(content, textEnd);
        }

        return spans;
    }

    // Whether a line holds only whitespace and comments, tracking whether it ends inside a /* */ comment.
    private static bool IsTrivia(string line, ref bool inComment)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (inComment)
            {
                var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    return true;
                }

                inComment = false;
                i = end + 2;
            }
            else if (char.IsWhiteSpace(line[i]))
            {
                i++;
            }
            else if (line.AsSpan(i).StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }
            else if (line.AsSpan(i).StartsWith("/*", StringComparison.Ordinal))
            {
                inComment = true;
                i += 2;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    // The files a full path or glob (*, ? and **, as MSBuild matches them) names, or null when a directory the glob
    // searches can't be read.
    private static List<string>? MatchFiles(IFileSystem fileSystem, string pattern)
    {
        var path = pattern.Replace('\\', '/');
        var wildcard = path.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            var fullPath = fileSystem.Path.GetFullPath(path);
            return fileSystem.File.Exists(fullPath) ? [fullPath]
                : IsMissing(fileSystem, fullPath) ? []
                : null;
        }

        var rootEnd = path.LastIndexOf('/', wildcard);
        var root = fileSystem.Path.GetFullPath(path[..(rootEnd + 1)]);
        var glob = path[(rootEnd + 1)..];
        if (!fileSystem.Directory.Exists(root))
        {
            return IsMissing(fileSystem, root) ? [] : null;
        }

        var globRegex = GlobRegex(glob);
        var searchOption = glob.Contains('/') || glob.Contains("**", StringComparison.Ordinal)
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            return [
                .. fileSystem.Directory.EnumerateFiles(root, "*", searchOption)
                    .Where(file => globRegex.IsMatch(fileSystem.Path.GetRelativePath(root, file).Replace('\\', '/')))
                    .Select(file => fileSystem.Path.GetFullPath(file))
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory under the glob that can't be read, which may hold files the build compiles.
            return null;
        }
    }

    // Whether the path is known not to exist: the nearest directory above it that exists lists nothing on the way to
    // it. A path that exists but can't be reached (a directory on the way can't be read) isn't missing.
    private static bool IsMissing(IFileSystem fileSystem, string path)
    {
        var child = fileSystem.Path.TrimEndingDirectorySeparator(path);
        var parent = fileSystem.Path.GetDirectoryName(child);
        while (parent != null && !fileSystem.Directory.Exists(parent))
        {
            child = parent;
            parent = fileSystem.Path.GetDirectoryName(parent);
        }

        if (parent == null)
        {
            return true;
        }

        try
        {
            // Where case is ignored, an entry differing only in case would have been found above, so one listed
            // differing in case is another file.
            var name = fileSystem.Path.GetFileName(child);
            return !fileSystem.Directory.EnumerateFileSystemEntries(parent)
                .Any(entry => string.Equals(fileSystem.Path.GetFileName(entry), name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

        // The glob comes from the app's files, so it is matched in time linear in the path's length.
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(builder.Append('$').ToString(), options);
    }
}
