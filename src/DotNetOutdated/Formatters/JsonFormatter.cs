using DotNetOutdated.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DotNetOutdated.Formatters;

internal class JsonFormatter : IOutputFormatter
{
    public void Format(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        var report = new Report(projects);
        JsonSerializer serializer = JsonSerializer.CreateDefault(default);
        serializer.Formatting = Formatting.Indented;
        serializer.Serialize(writer, report);
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
