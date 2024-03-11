using CsvHelper;
using DotNetOutdated.Models;
using McMaster.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;

namespace DotNetOutdated.Formatters;

internal class CsvFormatter : FileFormatter
{
    public CsvFormatter(IFileSystem fileSystem, IConsole console) : base(fileSystem, console)
    {
    }

    protected override string Extension => ".csv";

    protected override void Format(IReadOnlyList<AnalyzedProject> projects, IDictionary<string, string> options, TextWriter writer)
    {
        using var csv = new CsvWriter(writer, CultureInfo.CurrentCulture);
        foreach (var project in projects)
        {
            foreach (var targetFramework in project.TargetFrameworks)
            {
                foreach (var dependency in targetFramework.Dependencies)
                {
                    var upgradeSeverity = Enum.GetName(dependency.UpgradeSeverity);

                    csv.WriteRecord(new
                    {
                        ProjectName = project.Name,
                        TargetFrameworkName = targetFramework.Name.DotNetFrameworkName,
                        DependencyName = dependency.Name,
                        ResolvedVersion = dependency.ResolvedVersion?.ToString(),
                        LatestVersion = dependency.LatestVersion?.ToString(),
                        UpgradeSeverity = upgradeSeverity
                    });
                    csv.NextRecord();
                }
            }
        }
    }
}
