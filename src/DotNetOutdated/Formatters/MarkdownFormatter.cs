#nullable enable
using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Formatters;

internal class MarkdownFormatter : IOutputFormatter
{
    static readonly Dictionary<DependencyUpgradeSeverity, string?> _colorMaps = new()
    {
        {DependencyUpgradeSeverity.None,default},
        {DependencyUpgradeSeverity.Patch,"green"},
        {DependencyUpgradeSeverity.Minor,"yellow"},
        {DependencyUpgradeSeverity.Major,"red"},
        {DependencyUpgradeSeverity.Unknown,default},
    };

    public void Format(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        writer.WriteLine("# Outdated Packages");
        writer.WriteLine();
        foreach (var project in projects.OrderBy(p=>p.Name))
        {
            writer.WriteLine($"## {project.Name}");
            writer.WriteLine();
            foreach (var targetFramework in project.TargetFrameworks)
            {
                writer.WriteLine($"### Target:{targetFramework.Name}");
                writer.WriteLine();
                writer.WriteLine("|Package|Transitive|Current|Last|Severity|");
                writer.WriteLine("|-|-|-:|-:|-:|");
                foreach (var dependency in targetFramework.Dependencies)
                {
                    writer.Write('|');
                    writer.Write(dependency.Name);
                    writer.Write('|');
                    writer.Write(dependency.IsTransitive.ToString());
                    writer.Write('|');
                    writer.Write(dependency.ResolvedVersion);
                    writer.Write('|');
                    if (GetFormattedLatestVersion(dependency) is { } latestVersion)
                    {
                        writer.Write('$');
                        if (!string.IsNullOrWhiteSpace(latestVersion.matching))
                        {
                            writer.Write(@"{\textsf{");
                            writer.Write(latestVersion.matching);
                            writer.Write('}');
                            writer.Write('}');
                        }
                        if (!string.IsNullOrWhiteSpace(latestVersion.color))
                        {
                            writer.Write(@"\textcolor{");
                            writer.Write(latestVersion.color);
                            writer.Write('}');
                        }
                        if (!string.IsNullOrWhiteSpace(latestVersion.rest))
                        {
                            writer.Write(@"{\textsf{");
                            writer.Write(latestVersion.rest);
                            writer.Write('}');
                            writer.Write('}');
                        }
                        writer.Write('$');
                    }
                    writer.Write('|');
                    writer.WriteLine(dependency.UpgradeSeverity);
                }
                writer.WriteLine();
            }
        }

        // Note
        writer.WriteLine("> __Note__");
        writer.WriteLine('>');
        writer.WriteLine("> 🔴: Major version update or pre-release version. Possible breaking changes.");
        writer.WriteLine('>');
        writer.WriteLine("> 🟡: Minor version update. Backwards-compatible features added.");
        writer.WriteLine('>');
        writer.WriteLine("> 🟢: Patch version update. Backwards-compatible bug fixes.");
    }

    private static (string? color, string? matching, string? rest)? GetFormattedLatestVersion(AnalyzedDependency dependency)
    {
        if (dependency.LatestVersion is { } latestVersion)
        {
            var latestString = latestVersion.ToString();
            _colorMaps.TryGetValue(dependency.UpgradeSeverity, out var color);
            if (dependency.ResolvedVersion is { } resolvedVersion)
            {
                if (resolvedVersion.IsPrerelease)
                {
                    return (color, default, latestString);
                }
                else
                {
                    var matching = string.Join(".", resolvedVersion.GetParts()
                        .Zip(latestVersion.GetParts(), (p1, p2) => (part: p2, matches: p1 == p2))
                        .TakeWhile(p => p.matches)
                        .Select(p => p.part));
                    if (matching.Length > 0) { matching += "."; }
                    var rest = new Regex($"^{matching}").Replace(latestString, "");
                    return (color, matching, rest);
                }
            }
            else
            {
                return (default, latestString, default);
            }
        }
        return default;
    }
}
