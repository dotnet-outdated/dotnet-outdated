using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class JsonFormatter : IOutputFormatter
{
    private static readonly JsonSerializerOptions jsonSerializerOptions = new() { WriteIndented = true };

    public async Task FormatAsync(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        var report = new Report(projects);

        var json = JsonSerializer.Serialize(report, jsonSerializerOptions);
        await writer.WriteAsync(json).ConfigureAwait(false);
    }

    private class Report(IReadOnlyList<AnalyzedProject> projects)
    {
        public IReadOnlyList<AnalyzedProject> Projects { get; set; } = projects;

        internal static string GetTextReportLine(AnalyzedProject project, AnalyzedTargetFramework targetFramework, AnalyzedDependency dependency)
        {
            var upgradeSeverity = Enum.GetName(dependency.UpgradeSeverity);
            return string.Format(CultureInfo.InvariantCulture, "{0};{1};{2};{3};{4};{5}",
                project.Name,
                targetFramework.Name,
                dependency.Name,
                dependency.ResolvedVersion,
                dependency.LatestVersion,
                upgradeSeverity);
        }
    }
}
