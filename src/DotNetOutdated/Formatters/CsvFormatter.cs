using CsvHelper;
using DotNetOutdated.Models;
using McMaster.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

internal class CsvFormatter(IFileSystem fileSystem, IConsole console)
    : FileFormatter(fileSystem, console)
{
    protected override string Extension => ".csv";

    internal protected async override Task FormatAsync(IReadOnlyList<AnalyzedProject> projects
        , IDictionary<string, string> options
        , TextWriter writer)
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