using CsvHelper;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class CsvFormatter : IOutputFormatter
{
    public async Task FormatAsync(IReadOnlyList<OutdatedProject> projects, TextWriter writer)
    {
        // CsvHelper 34 no longer derives the separator from the culture; keep the
        // culture's list separator (e.g. ';' for de-DE) as earlier versions did.
        var listSeparator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        var options = CsvWriterOptions.Default with
        {
            FormatProvider = CultureInfo.CurrentCulture,
            Separator = listSeparator.Length == 1 && char.IsAscii(listSeparator[0]) ? listSeparator[0] : ',',
        };

        var csv = CsvWriter.Create(writer, options);
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
                            DependencyName = dependency.Dependency.Name,
                            ResolvedVersion = dependency.Dependency.ResolvedVersion?.ToString(),
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