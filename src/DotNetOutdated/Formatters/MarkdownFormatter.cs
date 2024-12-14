#nullable enable
using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    public async Task FormatAsync(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Outdated Packages");
        sb.AppendLine();
        foreach (var project in projects.OrderBy(p => p.Name))
        {
            sb.AppendLine($"## {project.Name}");
            sb.AppendLine();
            foreach (var targetFramework in project.TargetFrameworks)
            {
                sb.AppendLine($"### Target:{targetFramework.Name}");
                sb.AppendLine();
                sb.AppendLine("|Package|Transitive|Current|Last|Severity|");
                sb.AppendLine("|-|-|-:|-:|-:|");
                foreach (var dependency in targetFramework.Dependencies)
                {
                    sb.Append('|');
                    sb.Append(dependency.Name);
                    sb.Append('|');
                    sb.Append(dependency.IsTransitive.ToString());
                    sb.Append('|');
                    sb.Append(dependency.ResolvedVersion);
                    sb.Append('|');
                    if (GetFormattedLatestVersion(dependency) is { } latestVersion)
                    {
                        sb.Append('$');
                        if (!string.IsNullOrWhiteSpace(latestVersion.matching))
                        {
                            sb.Append(@"{\textsf{");
                            sb.Append(latestVersion.matching);
                            sb.Append('}');
                            sb.Append('}');
                        }
                        if (!string.IsNullOrWhiteSpace(latestVersion.color))
                        {
                            sb.Append(@"\textcolor{");
                            sb.Append(latestVersion.color);
                            sb.Append('}');
                        }
                        if (!string.IsNullOrWhiteSpace(latestVersion.rest))
                        {
                            sb.Append(@"{\textsf{");
                            sb.Append(latestVersion.rest);
                            sb.Append('}');
                            sb.Append('}');
                        }
                        sb.Append('$');
                    }
                    sb.Append('|');
                    sb.AppendLine(dependency.UpgradeSeverity.ToString());
                }
                sb.AppendLine();
            }
        }

        // Note
        sb.AppendLine("> __Note__");
        sb.Append('>').AppendLine();
        sb.AppendLine("> 🔴: Major version update or pre-release version. Possible breaking changes.");
        sb.Append('>').AppendLine();
        sb.AppendLine("> 🟡: Minor version update. Backwards-compatible features added.");
        sb.Append('>').AppendLine();
        sb.AppendLine("> 🟢: Patch version update. Backwards-compatible bug fixes.");

        await writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
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
