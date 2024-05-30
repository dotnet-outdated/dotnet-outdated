using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Formatters;
using DotNetOutdated.Models;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

public class JsonFormatterTests
{
    [Fact]
    public async Task JsonFormatterReportOutput()
    {
        // Testing the JSON format, as the output is effectively a contract
      
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var previewVersion = new NuGetVersion("9.0.0-preview.4.24261.1");
        var newerPreviewVersion = new NuGetVersion("9.0.0-preview.4.24263.5");
        
        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Microsoft.Extensions.Http.Diagnostics", new VersionRange(previewVersion), previewVersion, false, false, false, true), newerPreviewVersion),
                    new AnalyzedDependency(new Dependency("Microsoft.Extensions.Http.Resilience", new VersionRange(previewVersion), previewVersion, false, false, false, true), newerPreviewVersion),
                    new AnalyzedDependency(new Dependency("Microsoft.Extensions.Telemetry", new VersionRange(previewVersion), previewVersion, false, false, false, true), newerPreviewVersion)
                })
            })
        ];
        
        var json = new JsonFormatter();
        await json.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          {
            "Projects": [
              {
                "Name": "TweetiePie",
                "FilePath": "C:\\Coding\\codeflow\\tweetiepie\\src\\TweetiePie\\TweetiePie.csproj",
                "TargetFrameworks": [
                  {
                    "Name": "net9.0",
                    "Dependencies": [
                      {
                        "Name": "Microsoft.Extensions.Http.Diagnostics",
                        "ResolvedVersion": "9.0.0-preview.4.24261.1",
                        "LatestVersion": "9.0.0-preview.4.24263.5",
                        "UpgradeSeverity": "Major"
                      },
                      {
                        "Name": "Microsoft.Extensions.Http.Resilience",
                        "ResolvedVersion": "9.0.0-preview.4.24261.1",
                        "LatestVersion": "9.0.0-preview.4.24263.5",
                        "UpgradeSeverity": "Major"
                      },
                      {
                        "Name": "Microsoft.Extensions.Telemetry",
                        "ResolvedVersion": "9.0.0-preview.4.24261.1",
                        "LatestVersion": "9.0.0-preview.4.24263.5",
                        "UpgradeSeverity": "Major"
                      }
                    ]
                  }
                ]
              }
            ]
          }
          """;
        
        Assert.Equal(expectedReport, stringBuilder.ToString());
    }
}