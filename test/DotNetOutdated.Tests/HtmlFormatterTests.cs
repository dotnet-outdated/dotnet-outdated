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

public class HtmlFormatterTests
{
    private const string _noteString =
        """
        <blockquote>
        <p><strong>Note</strong></p>
        <p>🔴: Major version update or pre-release version. Possible breaking changes.</p>
        <p>🟠: Minor version update. Backwards-compatible features added.</p>
        <p>🟢: Patch version update. Backwards-compatible bug fixes.</p>
        </blockquote>

        """;

    [Fact]
    public async Task NoUpdatesShowsCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var previewVersion = new NuGetVersion(new System.Version(1, 2, 3, 4));
        var newerPreviewVersion = new NuGetVersion(new System.Version(1, 2, 3, 4));

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(previewVersion), previewVersion, false, false, false, true), newerPreviewVersion)
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">1.2.3.4</td>
          <td style="text-align: right;"><span>1.2.3.4</span></td>
          <td style="text-align: right;">None</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task PatchUpdateShowsCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var resolvedVersion = new NuGetVersion(new System.Version(1, 2, 2, 4));
        var latestVersion = new NuGetVersion(new System.Version(1, 2, 3, 4));

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(resolvedVersion), resolvedVersion, false, false, false, true), latestVersion)
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">1.2.2.4</td>
          <td style="text-align: right;"><span>1.2.</span><span style="color:green;font-weight:bold">3.4</span></td>
          <td style="text-align: right;">Patch</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task MinorUpdateShowsCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var resolvedVersion = new NuGetVersion(new System.Version(1, 1, 3, 4));
        var latestVersion = new NuGetVersion(new System.Version(1, 2, 3, 4));

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(resolvedVersion), resolvedVersion, false, false, false, true), latestVersion)
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">1.1.3.4</td>
          <td style="text-align: right;"><span>1.</span><span style="color:orange;font-weight:bold">2.3.4</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task MajorUpdateShowsCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var resolvedVersion = new NuGetVersion(new System.Version(0, 2, 3, 4));
        var latestVersion = new NuGetVersion(new System.Version(1, 2, 3, 4));

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(resolvedVersion), resolvedVersion, false, false, false, true), latestVersion)
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">0.2.3.4</td>
          <td style="text-align: right;"><span></span><span style="color:red;font-weight:bold">1.2.3.4</span></td>
          <td style="text-align: right;">Major</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task NotCentrallyUpdatedShowsCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        var resolvedVersion = new NuGetVersion(new System.Version(1, 2, 3));
        var latestVersion = new NuGetVersion(new System.Version(1, 3, 4));

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(resolvedVersion), resolvedVersion, false, false, false, false), latestVersion)
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">1.2.3</td>
          <td style="text-align: right;"><span>1.</span><span style="color:orange;font-weight:bold">3.4</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task MultipleUpdatesShowCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(new NuGetVersion(9, 0, 1)), new NuGetVersion(9, 0, 1), false, false, false, false), new NuGetVersion(9, 1, 2)),
                    new AnalyzedDependency(new Dependency("Package.Example.Client", new VersionRange(new NuGetVersion(6, 2, 8)), new NuGetVersion(6, 2, 8), false, false, false, false), new NuGetVersion(6, 2, 9))
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">9.0.1</td>
          <td style="text-align: right;"><span>9.</span><span style="color:orange;font-weight:bold">1.2</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          <tr>
          <td>Package.Example.Client</td>
          <td>False</td>
          <td style="text-align: right;">6.2.8</td>
          <td style="text-align: right;"><span>6.2.</span><span style="color:green;font-weight:bold">9</span></td>
          <td style="text-align: right;">Patch</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task MultipleFrameworksShowCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(new NuGetVersion(9, 0, 1)), new NuGetVersion(9, 0, 1), false, false, false, false), new NuGetVersion(9, 1, 2)),
                }),
                new AnalyzedTargetFramework(NuGetFramework.Parse("net8.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(new NuGetVersion(9, 0, 1)), new NuGetVersion(9, 0, 1), false, false, false, false), new NuGetVersion(9, 1, 2)),
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">9.0.1</td>
          <td style="text-align: right;"><span>9.</span><span style="color:orange;font-weight:bold">1.2</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          </tbody></table>
          <h3>Target:net8.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">9.0.1</td>
          <td style="text-align: right;"><span>9.</span><span style="color:orange;font-weight:bold">1.2</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }

    [Fact]
    public async Task MultipleProjectsShowCorrectly()
    {
        var stringBuilder = new StringBuilder();
        var textWriter = new StringWriter(stringBuilder);

        List<AnalyzedProject> analyzedProjects =
        [
            new AnalyzedProject("TweetiePie", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie\TweetiePie.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example", new VersionRange(new NuGetVersion(9, 0, 1)), new NuGetVersion(9, 0, 1), false, false, false, false), new NuGetVersion(9, 1, 2)),
                })
            }),
            new AnalyzedProject("TweetiePie.API", @"C:\Coding\codeflow\tweetiepie\src\TweetiePie.API\TweetiePie.API.csproj", new List<AnalyzedTargetFramework>
            {
                new AnalyzedTargetFramework(NuGetFramework.Parse("net9.0"), new List<AnalyzedDependency>
                {
                    new AnalyzedDependency(new Dependency("Package.Example.Client", new VersionRange(new NuGetVersion(6, 2, 8)), new NuGetVersion(6, 2, 8), false, false, false, false), new NuGetVersion(6, 2, 9))
                })
            })
        ];

        var html = new HtmlFormatter();
        await html.FormatAsync(analyzedProjects, textWriter);

        const string expectedReport =
          """
          <h1>Outdated Packages</h1>
          <h2>TweetiePie</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example</td>
          <td>False</td>
          <td style="text-align: right;">9.0.1</td>
          <td style="text-align: right;"><span>9.</span><span style="color:orange;font-weight:bold">1.2</span></td>
          <td style="text-align: right;">Minor</td>
          </tr>
          </tbody></table>
          <h2>TweetiePie.API</h2>
          <h3>Target:net9.0</h3>
          <table><thead><tr>
          <th>Package</th>
          <th>Transitive</th>
          <th style="text-align: right;">Current</th>
          <th style="text-align: right;">Last</th>
          <th style="text-align: right;">Severity</th>
          </tr></thead>
          <tbody>
          <tr>
          <td>Package.Example.Client</td>
          <td>False</td>
          <td style="text-align: right;">6.2.8</td>
          <td style="text-align: right;"><span>6.2.</span><span style="color:green;font-weight:bold">9</span></td>
          <td style="text-align: right;">Patch</td>
          </tr>
          </tbody></table>

          """ + _noteString;

        Assert.Equal(expectedReport, stringBuilder.ToString());
    }
}