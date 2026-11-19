using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services;

/// <summary>A C# file a file-based app compiles, and its file-level directive lines.</summary>
internal sealed record FileBasedAppSourceFile(string Path, IReadOnlyList<string> Directives);

/// <summary>
/// The C# files a .NET file-based app compiles and the properties its directives see, found the way the SDK finds
/// them: its entry point, then each <c>.cs</c> file the <c>#:include</c> and <c>#:exclude</c> directives of the files
/// read so far leave as a Compile item, read in turn. Only a file's leading directives count, as for the SDK: those
/// before its first C# token or <c>#if</c>, outside comments.
/// </summary>
/// <param name="Files">The entry point, then the included files in the order they were found.</param>
/// <param name="Properties">The imported properties, overridden by the files' <c>#:property</c> lines.</param>
/// <param name="HasIncludes">Whether any of the files has an <c>#:include</c> directive.</param>
/// <param name="UnresolvedIncludes">The include and exclude directives whose path uses a property that isn't defined.</param>
internal sealed partial record FileBasedAppSources(
    IReadOnlyList<FileBasedAppSourceFile> Files,
    Dictionary<string, (string Value, string FilePath)> Properties,
    bool HasIncludes,
    IReadOnlyList<string> UnresolvedIncludes)
{
    /// <summary>Compares file paths as the SDK does when it tracks the files it has read.</summary>
    public static readonly StringComparer PathComparer = StringComparer.Ordinal;

    [GeneratedRegex(@"^[ \t]*#:[ \t]*(include|exclude)[ \t]+(.+?)\s*$")]
    private static partial Regex IncludeDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*property[ \t]+([^=\s]+)[ \t]*=[ \t]*(.*?)\s*$")]
    private static partial Regex PropertyDirectiveLineRegex();

    [GeneratedRegex(@"^#[ \t]*if\b")]
    private static partial Regex IfDirectiveRegex();

    public static FileBasedAppSources Read(IFileSystem fileSystem, IProjectEvaluator evaluator, string projectFilePath)
    {
        var properties = new Dictionary<string, (string Value, string FilePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in evaluator.GetImportedProperties(projectFilePath))
        {
            properties[name] = (definition.RawValue, definition.FilePath);
        }

        var entryPoint = fileSystem.Path.GetFullPath(projectFilePath);
        var files = new List<FileBasedAppSourceFile>();
        var seen = new HashSet<string>(PathComparer) { entryPoint };
        var toRead = new Queue<string>([entryPoint]);

        // The Compile items the include and exclude directives read so far leave, in order: the SDK writes them as
        // ordered Include and Remove items, so a later include can add back what an earlier exclude removed. A file
        // once read keeps its directives, as for the SDK, even if a later exclude removes it.
        var compileItems = new List<string>();
        var hasIncludes = false;
        var unresolved = new List<string>();
        while (toRead.TryDequeue(out var path))
        {
            var file = new FileBasedAppSourceFile(path, DirectiveLines(fileSystem.File.ReadAllText(path)));
            files.Add(file);
            foreach (var line in file.Directives)
            {
                CollectProperty(line, path, properties);
            }

            var directory = fileSystem.Path.GetDirectoryName(path)!;
            foreach (var line in file.Directives)
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
                    unresolved.Add($"'#:{match.Groups[1].Value} {pattern}' in '{path}'");
                    continue;
                }

                var matches = MatchFiles(fileSystem, resolvedPattern, directory);
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

            foreach (var item in compileItems.Where(seen.Add))
            {
                toRead.Enqueue(item);
            }
        }

        return new FileBasedAppSources(files, properties, hasIncludes, unresolved);
    }

    /// <summary>
    /// Replaces the matches of <paramref name="regex"/> in the file-level directive lines of <paramref name="content"/>
    /// only, leaving text in comments and code alone.
    /// </summary>
    public static string ReplaceInDirectives(string content, Regex regex, MatchEvaluator evaluator)
    {
        var builder = new StringBuilder(content.Length);
        var copied = 0;
        foreach (var (start, end) in DirectiveLineSpans(content))
        {
            builder.Append(content, copied, start - copied);
            builder.Append(regex.Replace(content[start..end], evaluator));
            copied = end;
        }

        return builder.Append(content, copied, content.Length - copied).ToString();
    }

    /// <summary>Adds a directive line after the file's last directive (or shebang), or as its first line.</summary>
    public static string InsertDirective(string content, string directive)
    {
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var spans = DirectiveLineSpans(content);
        if (spans.Count == 0)
        {
            return directive + newLine + content;
        }

        var lineBreak = content.IndexOf('\n', spans[^1].End);
        return lineBreak < 0
            ? content + newLine + directive + newLine
            : content[..(lineBreak + 1)] + directive + newLine + content[(lineBreak + 1)..];
    }

    /// <summary>
    /// The value of <paramref name="expression"/> with its <c>$(Name)</c> references expanded; <see langword="false"/>
    /// when one isn't defined (or the expansion doesn't finish).
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
                return true;
            }
        }

        resolvedValue = current;
        return !VersionExpression.PropertyReferenceRegex().IsMatch(current);
    }

    private static void CollectProperty(string line, string filePath, Dictionary<string, (string Value, string FilePath)> properties)
    {
        var match = PropertyDirectiveLineRegex().Match(line);
        if (match.Success)
        {
            // Later #:property lines override earlier ones (MSBuild-like semantics).
            properties[match.Groups[1].Value] = (match.Groups[2].Value, filePath);
        }
    }

    private static List<string> DirectiveLines(string content) =>
        [.. DirectiveLineSpans(content).Select(span => content[span.Start..span.End])];

    // The start and end (before its line break) of each #: or #! line before the file's first C# token or #if,
    // outside comments: the lines the SDK reads directives from.
    private static List<(int Start, int End)> DirectiveLineSpans(string content)
    {
        var spans = new List<(int Start, int End)>();
        var inComment = false;
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineBreak = content.IndexOf('\n', lineStart);
            var lineEnd = lineBreak < 0 ? content.Length : lineBreak;
            var textEnd = lineEnd > lineStart && content[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
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

            lineStart = lineEnd + 1;
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
    private static List<string> MatchFiles(IFileSystem fileSystem, string pattern, string baseDirectory)
    {
        var path = fileSystem.Path.Combine(baseDirectory, pattern).Replace('\\', '/');
        var wildcard = path.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            var fullPath = fileSystem.Path.GetFullPath(path);
            return fullPath.IsCSharpFile() && fileSystem.File.Exists(fullPath) ? [fullPath] : [];
        }

        var rootEnd = path.LastIndexOf('/', wildcard);
        var root = fileSystem.Path.GetFullPath(rootEnd < 0 ? baseDirectory : path[..(rootEnd + 1)]);
        var glob = path[(rootEnd + 1)..];
        if (!fileSystem.Directory.Exists(root))
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
                .. fileSystem.Directory.EnumerateFiles(root, "*", searchOption)
                    .Where(file => file.IsCSharpFile() && globRegex.IsMatch(fileSystem.Path.GetRelativePath(root, file).Replace('\\', '/')))
                    .Select(file => fileSystem.Path.GetFullPath(file))
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
}
