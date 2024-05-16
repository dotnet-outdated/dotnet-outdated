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
                        var upgradeSeverity = Enum.GetName(dependency.UpgradeSeverity);

                        records.Add(new CsvDependency
                        {
                            ProjectName = project.Name,
                            TargetFrameworkName = targetFramework.Name.DotNetFrameworkName,
                            DependencyName = dependency.Name,
                            ResolvedVersion = dependency.ResolvedVersion?.ToString(),
                            LatestVersion = dependency.LatestVersion?.ToString(),
                            UpgradeSeverity = upgradeSeverity
                        });
                    }
                }
            }

            await csv.WriteRecordsAsync(records).ConfigureAwait(false);
        }
    }
}