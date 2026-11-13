using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Formatters;
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
        
        List<OutdatedProject> analyzedProjects =
        [
            new OutdatedProject
            {
                Name = "TweetiePie",
                FilePath = @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj",
                TargetFrameworks =
                [
                    new OutdatedTargetFramework
                    {
                        Name = NuGetFramework.Parse("net9.0"),
                        Dependencies =
                        [
                            new OutdatedDependency { Dependency = new Dependency { Name = "Microsoft.Extensions.Http.Diagnostics", VersionRange = new VersionRange(previewVersion), ResolvedVersion = previewVersion }, LatestVersion = newerPreviewVersion },
                            new OutdatedDependency { Dependency = new Dependency { Name = "Microsoft.Extensions.Http.Resilience", VersionRange = new VersionRange(previewVersion), ResolvedVersion = previewVersion }, LatestVersion = newerPreviewVersion },
                            new OutdatedDependency { Dependency = new Dependency { Name = "Microsoft.Extensions.Telemetry", VersionRange = new VersionRange(previewVersion), ResolvedVersion = previewVersion }, LatestVersion = newerPreviewVersion },
                        ],
                    },
                ],
            },
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