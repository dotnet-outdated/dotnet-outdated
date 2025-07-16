using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
    [InlineData("development-dependencies-lock",  "", 0)]
    [InlineData("development-dependencies-lock",  "linux-x64", 0)]
    [InlineData("development-dependencies-lock",  "windows-x64", 1)]
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

    [Theory]
    [InlineData("8")]
    [InlineData("8.0")]
    [InlineData("System.Text.Json:8")]
    [InlineData("System.Text.Json:8.0")]
    [InlineData("System:8")]
    [InlineData("System:8.0")]
    public static void Can_Upgrade_Project_With_Maximum_Version(string maximumVersion)
    {
        using var directory = TestSetup("max-version");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, $"--maximum-version:{maximumVersion}", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));

        foreach (var project in output.RootElement.GetProperty("Projects").EnumerateArray())
        {
            foreach (var tfm in project.GetProperty("TargetFrameworks").EnumerateArray())
            {
                foreach (var dependency in tfm.GetProperty("Dependencies").EnumerateArray())
                {
                    var latestVersionString = dependency.GetProperty("LatestVersion").GetString();

                    Assert.True(Version.TryParse(latestVersionString, out var latestVersion));
                    Assert.Equal(8, latestVersion.Major);
                    Assert.Equal(0, latestVersion.Minor);
                    Assert.NotEqual(0, latestVersion.Build);
                }
            }
        }
    }

    [Fact]
    public static void Can_Upgrade_Multiple_Projects_With_Differing_Maximum_Versions()
    {
        using var directory = TestSetup("max-versions");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, $"--maximum-version:System.Text.Json:8.0", "--maximum-version:Newtonsoft.Json:12", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));

        foreach (var project in output.RootElement.GetProperty("Projects").EnumerateArray())
        {
            foreach (var tfm in project.GetProperty("TargetFrameworks").EnumerateArray())
            {
                foreach (var dependency in tfm.GetProperty("Dependencies").EnumerateArray())
                {
                    var latestVersionString = dependency.GetProperty("LatestVersion").GetString();
                    var name = dependency.GetProperty("Name").GetString();

                    Assert.True(Version.TryParse(latestVersionString, out var latestVersion));

                    if (name == "Newtonsoft.Json")
                    {
                        Assert.Equal(12, latestVersion.Major);
                        Assert.Equal(0, latestVersion.Minor);
                        Assert.NotEqual(0, latestVersion.Build);
                    }
                    else
                    {
                        Assert.Equal(8, latestVersion.Major);
                        Assert.Equal(0, latestVersion.Minor);
                        Assert.NotEqual(0, latestVersion.Build);
                    }
                }
            }
        }
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
