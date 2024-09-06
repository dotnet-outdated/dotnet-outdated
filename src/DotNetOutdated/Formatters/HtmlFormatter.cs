#nullable enable
using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class HtmlFormatter : IOutputFormatter
{
    static readonly Dictionary<DependencyUpgradeSeverity, string?> _colorMaps = new()
    {
        {DependencyUpgradeSeverity.None,default},
        {DependencyUpgradeSeverity.Patch,"green"},
        {DependencyUpgradeSeverity.Minor,"orange"},
        {DependencyUpgradeSeverity.Major,"red"},
        {DependencyUpgradeSeverity.Unknown,default},
    };

    public async Task FormatAsync(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<h1>Outdated Packages</h1>");
        foreach (var project in projects.OrderBy(p => p.Name))
        {
            sb.AppendLine($"<h2>{project.Name}</h2>");
            foreach (var targetFramework in project.TargetFrameworks)
            {
                sb.AppendLine($"<h3>Target:{targetFramework.Name}</h3>");
                sb.AppendLine("<table><thead><tr>");

                // Headers
                sb.AppendLine("<th>Package</th>");
                sb.AppendLine("<th>Transitive</th>");
                sb.AppendLine("<th style=\"text-align: right;\">Current</th>");
                sb.AppendLine("<th style=\"text-align: right;\">Last</th>");
                sb.AppendLine("<th style=\"text-align: right;\">Severity</th>");
                sb.AppendLine("</tr></thead>");

                // Body
                sb.AppendLine("<tbody>");
                foreach (var dependency in targetFramework.Dependencies)
                {
                    sb.AppendLine("<tr>");
                    sb.AppendLine("<td>" + dependency.Name + "</td>");
                    sb.AppendLine("<td>" + dependency.IsTransitive.ToString() + "</td>");
                    sb.AppendLine("<td style=\"text-align: right;\">" + dependency.ResolvedVersion + "</td>");

                    sb.Append("<td style=\"text-align: right;\">");
                    if (dependency.LatestVersion != null)
                    {
                        if (dependency.ResolvedVersion == null || dependency.ResolvedVersion.Equals(dependency.LatestVersion))
                            sb.Append("<span>" + dependency.LatestVersion + "</span>");
                        else
                            RenderUpdatableDependency(sb, dependency);
                    }
                    sb.AppendLine("</td>");
                    sb.AppendLine("<td style=\"text-align: right;\">" + dependency.UpgradeSeverity + "</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</tbody></table>");
            }
        }

        // Note
        sb.AppendLine("<blockquote>");
        sb.AppendLine("<p><strong>Note</strong></p>");
        sb.AppendLine("<p>🔴: Major version update or pre-release version. Possible breaking changes.</p>");
        sb.AppendLine("<p>🟠: Minor version update. Backwards-compatible features added.</p>");
        sb.AppendLine("<p>🟢: Patch version update. Backwards-compatible bug fixes.</p>");
        sb.AppendLine("</blockquote>");

        await writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
    }

    private static void RenderUpdatableDependency(StringBuilder sb, AnalyzedDependency dependency)
    {
        var latestString = dependency.LatestVersion.ToString();
        var colour = _colorMaps[dependency.UpgradeSeverity];

        if (dependency.ResolvedVersion.IsPrerelease)
            WriteSpanWithColour(sb, colour, latestString);

        var matching = string.Join(".", dependency.ResolvedVersion.GetParts()
            .Zip(dependency.LatestVersion.GetParts(), (p1, p2) => (part: p2, matches: p1 == p2))
            .TakeWhile(p => p.matches)
            .Select(p => p.part));
        if (matching.Length > 0) { matching += "."; }
        var rest = latestString.Substring(matching.Length).ToString();

        sb.Append("<span>");
        sb.Append(matching);
        sb.Append("</span>");

        if (!string.IsNullOrEmpty(rest))
            WriteSpanWithColour(sb, colour, rest);
    }

    private static void WriteSpanWithColour(StringBuilder sb, string? colour, string text)
    {
        sb.Append("<span style=\"color:");
        sb.Append(colour);
        sb.Append(";font-weight:bold\">");
        sb.Append(text);
        sb.Append("</span>");
    }
}
