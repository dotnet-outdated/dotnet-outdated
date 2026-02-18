using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace DotNetOutdated.Tests;

public static class EndToEndTests
{
    [Theory]
    [InlineData("build-props")]
    [InlineData("development-dependencies")]
    [InlineData("multi-target", Skip = "Fails on Windows in GitHub Actions for some reason.")]
    public static void Can_Upgrade_Project(string testProjectName)
    {
        using var project = TestSetup(testProjectName);

        var actual = Program.Main([project.Path]);
        Assert.Equal(0, actual);
    }

    [Theory]
    [InlineData("development-dependencies-lock", "", 0)]
    [InlineData("development-dependencies-lock", "linux-x64", 0)]
    [InlineData("development-dependencies-lock", "windows-x64", 1)]
    public static void Can_Upgrade_Lock_Project(string testProjectName, string runtime, int expectedExitCode)
    {
        using var project = TestSetup(testProjectName);

        var list = new List<string> { project.Path };

        if (!string.IsNullOrEmpty(runtime))
        {
            list.Add($"--runtime {runtime}");
        }

        var actual = Program.Main([.. list]);
        Assert.Equal(expectedExitCode, actual);
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Markdown)]
    public static void All_Formatters_Succeed(OutputFormat format)
    {
        using var project = TestSetup("development-dependencies");

        var outputExtension = format switch
        {
            OutputFormat.Json => "json",
            OutputFormat.Csv => "csv",
            OutputFormat.Markdown => "md",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var outputPath = Path.Combine(project.Path, Path.ChangeExtension("output", outputExtension));

        var actual = Program.Main([project.Path, "--output", outputPath, "--output-format", format.ToString()]);
        Assert.Equal(0, actual);
    }

    [Fact]
    public static void Can_Upgrade_Project_With_Maximum_Version()
    {
        using var directory = TestSetup("max-version");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, "--upgrade", "--maximum-version:8.0", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        var output = JsonNode.Parse(File.ReadAllText(outputPath));

        foreach (var project in output["Projects"].AsArray())
        {
            foreach (var tfm in project["TargetFrameworks"].AsArray())
            {
                foreach (var dependency in tfm["Dependencies"].AsArray())
                {
                    var latestVersionString = (string)dependency["LatestVersion"];

                    Assert.True(Version.TryParse(latestVersionString, out var latestVersion));
                    Assert.Equal(8, latestVersion.Major);
                    Assert.Equal(0, latestVersion.Minor);
                    Assert.NotEqual(0, latestVersion.Build);
                }
            }
        }

        var csproj = XDocument.Load(Path.Combine(directory.Path, "Project.csproj"));
        var csprojDependencyVersions = csproj.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        Assert.StartsWith("8.0.", csprojDependencyVersions["System.Text.Json"]);
    }

    [Fact]
    public static void Can_Upgrade_Project_With_Version_Ranges()
    {
        using var directory = TestSetup("version-range");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, "--upgrade", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        var output = JsonNode.Parse(File.ReadAllText(outputPath));

        var jsonDependencyVersions = output["Projects"][0]["TargetFrameworks"][0]["Dependencies"].AsArray()
            .ToDictionary(d => (string)d["Name"], d => (string)d["LatestVersion"]);

        Assert.True(Version.TryParse(jsonDependencyVersions["System.Text.Json"], out var systemTextJsonVersion));
        Assert.True(systemTextJsonVersion > new Version(6, 0, 0));
        Assert.True(systemTextJsonVersion < new Version(8, 0, 0));

        Assert.True(Version.TryParse(jsonDependencyVersions["Microsoft.Extensions.DependencyInjection"], out var dependencyInjectionVersion));
        Assert.Equal(new Version(7, 0, 0), dependencyInjectionVersion);

        var csproj = XDocument.Load(Path.Combine(directory.Path, "version-range.csproj"));
        var csprojDependencyVersions = csproj.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        Assert.Equal($"[{systemTextJsonVersion}, 8.0.0)", csprojDependencyVersions["System.Text.Json"]);
        Assert.Equal("[7.0.0]", csprojDependencyVersions["Microsoft.Extensions.DependencyInjection"]);
    }

    private static TemporaryDirectory TestSetup(string testProjectName)
    {
        var solutionRoot = typeof(EndToEndTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First((p) => p.Key is "SolutionRoot")
            .Value;

        var projectPath = Path.Combine(solutionRoot, "test-projects", testProjectName);

        var temp = new TemporaryDirectory();

        foreach (var source in Directory.GetFiles(projectPath, "*", SearchOption.TopDirectoryOnly))
        {
            string destination = Path.Combine(temp.Path, Path.GetFileName(source));
            File.Copy(source, destination);
        }

        return temp;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = CreateTempSubdirectory();

        public string Path => _directory.FullName;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        public bool Exists() => _directory.Exists;

        private static DirectoryInfo CreateTempSubdirectory()
        {
            const string Prefix = "dotnet-bumper-";
            return Directory.CreateTempSubdirectory(Prefix);
        }
    }
}
