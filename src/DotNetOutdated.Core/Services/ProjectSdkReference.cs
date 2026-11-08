using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

#nullable enable

namespace DotNetOutdated.Core.Services;

/// <summary>
/// A versioned MSBuild project SDK declared in a project file as <c>&lt;Project Sdk="Id/Version"&gt;</c>,
/// <c>&lt;Sdk Name="Id" Version="Version" /&gt;</c> or <c>&lt;Import Project="..." Sdk="Id" Version="Version" /&gt;</c>.
/// </summary>
public sealed class ProjectSdkReference
{
    /// <summary>
    /// Gets the SDK package id.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the version declared in the project file.
    /// </summary>
    public required NuGetVersion ResolvedVersion { get; init; }

    /// <summary>
    /// Gets the version range used when resolving the latest version on NuGet feeds. The declared version is the
    /// inclusive floor with an open upper bound so newer versions can be discovered.
    /// </summary>
    public required VersionRange VersionRange { get; init; }
}

/// <summary>
/// Where an MSBuild project SDK is declared in a project file.
/// </summary>
public enum ProjectSdkDeclarationKind
{
    /// <summary><c>&lt;Project Sdk="Id[/Version]"&gt;</c>. One declaration per SDK in the semicolon-separated list.</summary>
    ProjectAttribute,

    /// <summary><c>&lt;Sdk Name="Id" Version="Version" /&gt;</c>.</summary>
    SdkElement,

    /// <summary><c>&lt;Import Project="Sdk.props" Sdk="Id" Version="Version" /&gt;</c>.</summary>
    ImportElement
}

/// <summary>
/// An MSBuild project SDK declared in a project file, as written. <see cref="Version"/> is <see langword="null"/> when
/// the declaration has no version, and may contain MSBuild property references such as <c>$(AspireVersion)</c>.
/// </summary>
public sealed record ProjectSdkDeclaration(string Name, string? Version, ProjectSdkDeclarationKind Kind);

/// <summary>
/// Discovers and updates versioned MSBuild project SDK references in project files.
/// Only literal versions in the project file itself are supported; SDK versions pinned via
/// <c>global.json</c> <c>msbuild-sdks</c>, MSBuild properties or imported files are ignored.
/// Discovery parses the file as XML. Updates edit the text in place so the rest of the file keeps its formatting.
/// </summary>
internal static partial class ProjectSdkReferenceHelper
{
    // A <Project>, <Sdk> or <Import> start tag. Quoted attribute values are consumed whole, so a '>' inside a
    // value such as Condition="'$(A)' > '0'" does not end the tag.
    [GeneratedRegex(@"<(Project|Sdk|Import)\b((?:[^>""']|""[^""]*""|'[^']*')*)>")]
    private static partial Regex SdkDeclarationTagRegex();

    [GeneratedRegex(@"([\w:.-]+)\s*=\s*(""[^""]*""|'[^']*')")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"<!--.*?-->|<!\[CDATA\[.*?\]\]>", RegexOptions.Singleline)]
    private static partial Regex CommentOrCDataRegex();

    /// <summary>
    /// Gets the dictionary key for storing an SDK reference in <see cref="Models.TargetFramework.Dependencies"/>,
    /// so an SDK and a package with the same id do not collide.
    /// </summary>
    public static string GetDependencyDictionaryKey(string name) =>
        FileBasedAppReferenceHelper.GetDependencyDictionaryKey(name, FileBasedAppReferenceKind.Sdk);

    /// <summary>
    /// Reads every MSBuild project SDK declared in a project file: the <c>Sdk</c> attribute of the root
    /// <c>&lt;Project&gt;</c>, <c>&lt;Sdk&gt;</c> elements and <c>&lt;Import Sdk="..."&gt;</c> elements, versioned or not.
    /// The file is parsed as XML, so SDKs inside comments are ignored.
    /// </summary>
    /// <returns>The declarations in document order, or an empty list when the file is missing, unreadable or not valid XML.</returns>
    public static IReadOnlyList<ProjectSdkDeclaration> GetDeclarations(IFileSystem fileSystem, string projectFilePath)
    {
        XElement? root;
        try
        {
            if (!fileSystem.File.Exists(projectFilePath))
            {
                return [];
            }

            root = XDocument.Parse(fileSystem.File.ReadAllText(projectFilePath)).Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }

        if (root == null || root.Name.LocalName != "Project")
        {
            return [];
        }

        var declarations = new List<ProjectSdkDeclaration>();

        foreach (var sdk in (root.Attribute("Sdk")?.Value ?? string.Empty).Split(';'))
        {
            var separatorIndex = sdk.IndexOf('/');
            var name = (separatorIndex >= 0 ? sdk[..separatorIndex] : sdk).Trim();
            if (name.Length > 0)
            {
                declarations.Add(new ProjectSdkDeclaration(
                    name,
                    separatorIndex >= 0 ? sdk[(separatorIndex + 1)..].Trim() : null,
                    ProjectSdkDeclarationKind.ProjectAttribute));
            }
        }

        foreach (var element in root.Descendants())
        {
            var kind = element.Name.LocalName switch
            {
                "Sdk" when element.Parent == root => ProjectSdkDeclarationKind.SdkElement,
                "Import" => ProjectSdkDeclarationKind.ImportElement,
                _ => (ProjectSdkDeclarationKind?)null
            };

            var name = kind == ProjectSdkDeclarationKind.SdkElement
                ? element.Attribute("Name")?.Value.Trim()
                : element.Attribute("Sdk")?.Value.Trim();

            if (kind != null && !string.IsNullOrEmpty(name))
            {
                declarations.Add(new ProjectSdkDeclaration(name, element.Attribute("Version")?.Value.Trim(), kind.Value));
            }
        }

        return declarations;
    }

    /// <summary>
    /// Discovers the versioned SDK references declared in a project file. Only literal versions are returned;
    /// unversioned and property-backed declarations are skipped.
    /// </summary>
    public static IReadOnlyList<ProjectSdkReference> Discover(IFileSystem fileSystem, string projectFilePath)
    {
        var references = new List<ProjectSdkReference>();

        foreach (var declaration in GetDeclarations(fileSystem, projectFilePath))
        {
            if (declaration.Version != null)
            {
                TryAdd(references, declaration.Name, declaration.Version);
            }
        }

        return references;
    }

    /// <summary>
    /// Updates the version of every declaration of the named SDK in a project file. Declarations inside XML comments
    /// and CDATA sections are left alone.
    /// </summary>
    /// <returns><see langword="true"/> when the project file was changed; otherwise <see langword="false"/>.</returns>
    public static bool TryUpdate(IFileSystem fileSystem, string projectFilePath, string name, NuGetVersion newVersion)
    {
        var content = ReadAllText(fileSystem, projectFilePath, out var encoding);

        // Match against a copy where comments and CDATA are blanked out with spaces. The copy has the same length
        // as the original, so every match index is also an index into the original content.
        var searchable = CommentOrCDataRegex().Replace(content, match => new string(' ', match.Length));
        var edits = new List<(int Index, int Length, string Value)>();

        var seenProject = false;
        foreach (Match tag in SdkDeclarationTagRegex().Matches(searchable))
        {
            var attributes = GetAttributes(tag.Groups[2]);
            switch (tag.Groups[1].Value)
            {
                // Only the root <Project> declares SDKs; skip any later tag with the same name.
                case "Project" when !seenProject:
                    seenProject = true;
                    if (attributes.TryGetValue("Sdk", out var quotedValue))
                    {
                        var sdks = Unquote(quotedValue.Value).Split(';').Select(sdk =>
                        {
                            var separatorIndex = sdk.IndexOf('/');
                            return separatorIndex > 0 && IsLiteralMatch(sdk[..separatorIndex], sdk[(separatorIndex + 1)..], name)
                                ? $"{sdk[..(separatorIndex + 1)]}{newVersion}"
                                : sdk;
                        });

                        edits.Add((quotedValue.Index, quotedValue.Length, $"{quotedValue.Value[0]}{string.Join(';', sdks)}{quotedValue.Value[0]}"));
                    }

                    break;

                // <Sdk Name="Id" Version="..." /> and <Import Project="Sdk.props" Sdk="Id" Version="..." />
                case "Sdk" or "Import":
                    var nameAttribute = tag.Groups[1].Value == "Sdk" ? "Name" : "Sdk";
                    if (attributes.TryGetValue(nameAttribute, out var quotedName) &&
                        attributes.TryGetValue("Version", out var quotedVersion) &&
                        IsLiteralMatch(Unquote(quotedName.Value), Unquote(quotedVersion.Value), name))
                    {
                        edits.Add((quotedVersion.Index, quotedVersion.Length, $"{quotedVersion.Value[0]}{newVersion}{quotedVersion.Value[0]}"));
                    }

                    break;
            }
        }

        var newContent = new StringBuilder(content);
        foreach (var (index, length, value) in edits.OrderByDescending(edit => edit.Index))
        {
            newContent.Remove(index, length).Insert(index, value);
        }

        if (string.Equals(content, newContent.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        fileSystem.File.WriteAllText(projectFilePath, newContent.ToString(), encoding);
        return true;
    }

    /// <summary>
    /// Gets the quoted values of the attributes in a start tag, keyed by attribute name. Each value's index is an index
    /// into the text the tag was matched in.
    /// </summary>
    private static Dictionary<string, (int Index, int Length, string Value)> GetAttributes(Group attributesText)
    {
        var attributes = new Dictionary<string, (int Index, int Length, string Value)>(StringComparer.Ordinal);
        foreach (Match attribute in AttributeRegex().Matches(attributesText.Value))
        {
            var value = attribute.Groups[2];
            attributes.TryAdd(attribute.Groups[1].Value, (attributesText.Index + value.Index, value.Length, value.Value));
        }

        return attributes;
    }

    /// <summary>
    /// Reads a text file and reports its encoding, so it can be written back with the same byte order mark.
    /// Files without a byte order mark are read and written as UTF-8 without one.
    /// </summary>
    private static string ReadAllText(IFileSystem fileSystem, string path, out Encoding encoding)
    {
        using var reader = new StreamReader(
            new MemoryStream(fileSystem.File.ReadAllBytes(path)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);

        var content = reader.ReadToEnd();
        encoding = reader.CurrentEncoding;
        return content;
    }

    private static void TryAdd(List<ProjectSdkReference> references, string name, string version)
    {
        name = name.Trim();
        version = version.Trim();

        if (name.Length == 0 ||
            FileBasedAppReferenceHelper.ContainsPropertyReference(name, version) ||
            !NuGetVersion.TryParse(version, out var resolvedVersion) ||
            references.Any(reference => string.Equals(reference.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        references.Add(new ProjectSdkReference
        {
            Name = name,
            ResolvedVersion = resolvedVersion,
            VersionRange = FileBasedAppReferenceHelper.CreateMinimumVersionRange(resolvedVersion)
        });
    }

    private static bool IsLiteralMatch(string name, string version, string expectedName) =>
        string.Equals(name.Trim(), expectedName, StringComparison.OrdinalIgnoreCase) &&
        NuGetVersion.TryParse(version.Trim(), out _);

    private static string Unquote(string quotedValue) => quotedValue[1..^1];
}
