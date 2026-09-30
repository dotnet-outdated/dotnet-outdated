using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;

#nullable enable

namespace DotNetOutdated.Core.Services;

/// <summary>
/// A versioned MSBuild project SDK declared in a project file, either as <c>&lt;Project Sdk="Id/Version"&gt;</c>
/// or as <c>&lt;Sdk Name="Id" Version="Version" /&gt;</c>.
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
/// Discovers and updates versioned MSBuild project SDK references in project files.
/// Only literal versions in the project file itself are supported; SDK versions pinned via
/// <c>global.json</c> <c>msbuild-sdks</c>, MSBuild properties or imported files are ignored.
/// Updates use regular expressions so the rest of the project file keeps its formatting.
/// </summary>
internal static partial class ProjectSdkReferenceHelper
{
    [GeneratedRegex(@"(<Project\b[^>]*?\bSdk\s*=\s*)(""[^""]*""|'[^']*')")]
    private static partial Regex ProjectSdkAttributeRegex();

    [GeneratedRegex(@"<Sdk\b[^>]*>")]
    private static partial Regex SdkElementRegex();

    [GeneratedRegex(@"(\bVersion\s*=\s*)(""[^""]*""|'[^']*')")]
    private static partial Regex VersionAttributeRegex();

    [GeneratedRegex(@"\bName\s*=\s*(""[^""]*""|'[^']*')")]
    private static partial Regex NameAttributeRegex();

    /// <summary>
    /// Gets the dictionary key for storing an SDK reference in <see cref="Models.TargetFramework.Dependencies"/>,
    /// so an SDK and a package with the same id do not collide.
    /// </summary>
    public static string GetDependencyDictionaryKey(string name) =>
        FileBasedAppReferenceHelper.GetDependencyDictionaryKey(name, FileBasedAppReferenceKind.Sdk);

    /// <summary>
    /// Discovers the versioned SDK references declared in a project file.
    /// </summary>
    public static IReadOnlyList<ProjectSdkReference> Discover(IFileSystem fileSystem, string projectFilePath)
    {
        if (!fileSystem.File.Exists(projectFilePath))
        {
            return [];
        }

        var content = fileSystem.File.ReadAllText(projectFilePath);
        var references = new List<ProjectSdkReference>();

        var projectSdk = ProjectSdkAttributeRegex().Match(content);
        if (projectSdk.Success)
        {
            foreach (var sdk in Unquote(projectSdk.Groups[2].Value).Split(';'))
            {
                var separatorIndex = sdk.IndexOf('/');
                if (separatorIndex > 0)
                {
                    TryAdd(references, sdk[..separatorIndex], sdk[(separatorIndex + 1)..]);
                }
            }
        }

        foreach (Match sdkElement in SdkElementRegex().Matches(content))
        {
            var name = NameAttributeRegex().Match(sdkElement.Value);
            var version = VersionAttributeRegex().Match(sdkElement.Value);
            if (name.Success && version.Success)
            {
                TryAdd(references, Unquote(name.Groups[1].Value), Unquote(version.Groups[2].Value));
            }
        }

        return references;
    }

    /// <summary>
    /// Updates the version of every declaration of the named SDK in a project file.
    /// </summary>
    /// <returns><see langword="true"/> when the project file was changed; otherwise <see langword="false"/>.</returns>
    public static bool TryUpdate(IFileSystem fileSystem, string projectFilePath, string name, NuGetVersion newVersion)
    {
        var content = fileSystem.File.ReadAllText(projectFilePath);

        var newContent = ProjectSdkAttributeRegex().Replace(content, match =>
        {
            var quotedValue = match.Groups[2].Value;
            var sdks = Unquote(quotedValue).Split(';').Select(sdk =>
            {
                var separatorIndex = sdk.IndexOf('/');
                return separatorIndex > 0 && IsLiteralMatch(sdk[..separatorIndex], sdk[(separatorIndex + 1)..], name)
                    ? $"{sdk[..(separatorIndex + 1)]}{newVersion}"
                    : sdk;
            });

            return $"{match.Groups[1].Value}{quotedValue[0]}{string.Join(';', sdks)}{quotedValue[0]}";
        }, count: 1);

        newContent = SdkElementRegex().Replace(newContent, sdkElement =>
        {
            var nameAttribute = NameAttributeRegex().Match(sdkElement.Value);
            var versionAttribute = VersionAttributeRegex().Match(sdkElement.Value);
            if (!nameAttribute.Success || !versionAttribute.Success ||
                !IsLiteralMatch(Unquote(nameAttribute.Groups[1].Value), Unquote(versionAttribute.Groups[2].Value), name))
            {
                return sdkElement.Value;
            }

            return VersionAttributeRegex().Replace(
                sdkElement.Value,
                version => $"{version.Groups[1].Value}{version.Groups[2].Value[0]}{newVersion}{version.Groups[2].Value[0]}",
                count: 1);
        });

        if (string.Equals(content, newContent, StringComparison.Ordinal))
        {
            return false;
        }

        fileSystem.File.WriteAllText(projectFilePath, newContent);
        return true;
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
