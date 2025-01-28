using DotNetOutdated.Models;
using McMaster.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class JsonFormatter(IFileSystem fileSystem, IConsole console)
    : FileFormatter(fileSystem, console)
{
    private static readonly JsonSerializerOptions jsonSerializerOptions = new() { WriteIndented = true };

    protected override string Extension => ".json";

    internal protected async override Task FormatAsync(IReadOnlyList<AnalyzedProject> projects
        , IDictionary<string, string> options
        , TextWriter writer)
    {
        var report = new Report
        {
            Projects = projects
        };

        var json = JsonSerializer.Serialize(report, jsonSerializerOptions);
        await writer.WriteAsync(json).ConfigureAwait(false);
    }

    private class Report
    {
        public IReadOnlyList<AnalyzedProject> Projects { get; set; }

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
