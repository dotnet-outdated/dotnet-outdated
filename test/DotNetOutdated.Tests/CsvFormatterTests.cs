using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Formatters;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

public class CsvFormatterTests
{
    private static List<OutdatedProject> CreateProjects()
    {
        var resolved = new NuGetVersion("1.0.0");

        return
        [
            new OutdatedProject
            {
                Name = "My, Project",
                FilePath = "/src/MyProject.csproj",
                TargetFrameworks =
                [
                    new OutdatedTargetFramework
                    {
                        Name = NuGetFramework.Parse("net9.0"),
                        Dependencies =
                        [
                            new OutdatedDependency { Dependency = new Dependency { Name = "Alpha", VersionRange = new VersionRange(resolved), ResolvedVersion = resolved }, LatestVersion = new NuGetVersion("2.0.0") },
                            new OutdatedDependency { Dependency = new Dependency { Name = "Beta", VersionRange = new VersionRange(resolved), ResolvedVersion = resolved } },
                        ],
                    },
                ],
            },
        ];
    }

    private static async Task<string> FormatAsync(CultureInfo culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            using var writer = new StringWriter();
            await new CsvFormatter().FormatAsync(CreateProjects(), writer);
            return writer.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task CsvFormatterReportOutput()
    {
        var csv = await FormatAsync(CultureInfo.GetCultureInfo("en-US"));

        const string expected =
            "ProjectName,TargetFrameworkName,DependencyName,ResolvedVersion,LatestVersion,UpgradeSeverity\r\n" +
            "\"My, Project\",\".NETCoreApp,Version=v9.0\",Alpha,1.0.0,2.0.0,Major\r\n" +
            "\"My, Project\",\".NETCoreApp,Version=v9.0\",Beta,1.0.0,,Unknown\r\n";

        Assert.Equal(expected, csv);
    }

    [Fact]
    public async Task CsvFormatterUsesCurrentCultureListSeparator()
    {
        var csv = await FormatAsync(CultureInfo.GetCultureInfo("de-DE"));

        const string expected =
            "ProjectName;TargetFrameworkName;DependencyName;ResolvedVersion;LatestVersion;UpgradeSeverity\r\n" +
            "My, Project;.NETCoreApp,Version=v9.0;Alpha;1.0.0;2.0.0;Major\r\n" +
            "My, Project;.NETCoreApp,Version=v9.0;Beta;1.0.0;;Unknown\r\n";

        Assert.Equal(expected, csv);
    }
}
