using CsvHelper;
using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DotNetOutdated.Formatters;

internal class CsvFormatter : IOutputFormatter
{
    public void Format(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
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
