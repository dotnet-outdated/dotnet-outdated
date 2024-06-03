using CsvHelper;
using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class CsvFormatter : IOutputFormatter
{
    public async Task FormatAsync(IReadOnlyList<AnalyzedProject> projects, TextWriter writer)
    {
        var csv = new CsvWriter(writer, CultureInfo.CurrentCulture);
        await using (csv.ConfigureAwait(false))
        {
            List<CsvDependency> records = [];

            foreach (var project in projects)
            {
                foreach (var targetFramework in project.TargetFrameworks)
                {
                    foreach (var dependency in targetFramework.Dependencies)
                    {
                        var projectName = project.Name;
                        var targetFrameworkName = targetFramework.Name.DotNetFrameworkName;
                        var dependencyName = dependency.Name;
                        var resolvedVersion = dependency.ResolvedVersion?.ToString();
                        var latestVersion = dependency.LatestVersion?.ToString();
                        var upgradeSeverity = Enum.GetName(dependency.UpgradeSeverity);

                        records.Add(new CsvDependency(projectName, targetFrameworkName, dependencyName)
                        {
                            ResolvedVersion = resolvedVersion,
                            LatestVersion = latestVersion,
                            UpgradeSeverity = upgradeSeverity
                        });
                    }
                }
            }

            await csv.WriteRecordsAsync(records).ConfigureAwait(false);
        }
    }
}