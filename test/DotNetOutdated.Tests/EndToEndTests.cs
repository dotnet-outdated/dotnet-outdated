using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    [InlineData("direct-reference-variables")]
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

    [RequiresFileBasedAppSdk]
    public static void Can_Analyze_File_Based_App()
    {
        using var project = TestSetup("file-based-app");

        var appPath = Path.Combine(project.Path, "app.cs");
        var outputPath = Path.Combine(project.Path, "output.json");

        var actual = Program.Main([appPath, "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));
        var analyzedProject = Assert.Single(output.RootElement.GetProperty("Projects").EnumerateArray());

        Assert.Equal("app.cs", analyzedProject.GetProperty("Name").GetString());
        Assert.Equal(Path.GetFullPath(appPath), analyzedProject.GetProperty("FilePath").GetString());

        var analyzedDependency = Assert.Single(
            analyzedProject
                .GetProperty("TargetFrameworks")
                .EnumerateArray()
                .SelectMany(targetFramework => targetFramework.GetProperty("Dependencies").EnumerateArray()),
            dependency => dependency.GetProperty("Name").GetString() == "Newtonsoft.Json");

        Assert.Equal("11.0.1", analyzedDependency.GetProperty("ResolvedVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(analyzedDependency.GetProperty("LatestVersion").GetString()));
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("file-based-app");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:property NewtonsoftJsonPackageId=Newtonsoft.Json", content);
        Assert.Contains("#:property NewtonsoftJsonPackageVersion=", content);
        Assert.Contains("#:package $(NewtonsoftJsonPackageId)@$(NewtonsoftJsonPackageVersion)", content);
        Assert.DoesNotContain("#:property NewtonsoftJsonPackageVersion=11.0.1", content);
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Direct_Package_Directive()
    {
        using var project = TestSetup("file-based-app-direct-package");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:package Newtonsoft.Json@", content);
        Assert.DoesNotContain("#:package Newtonsoft.Json@11.0.1", content);
    }

    [Fact]
    public static void Can_Upgrade_Project_With_Maximum_Version()
    {
        using var directory = TestSetup("max-version");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, "--maximum-version:8.0", "--output", outputPath, "--output-format:json"]);
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
    public static void Can_Upgrade_Direct_Reference_With_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("direct-reference-variables");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var projectFilePath = Directory.GetFiles(project.Path, "*.csproj").First();
        var content = File.ReadAllText(projectFilePath);

        Assert.Contains("Version=\"$(NewtonsoftJsonVersion)\"", content);
        Assert.Contains("Version=\"$(MicrosoftExtensionsVersion)\"", content);

        Assert.DoesNotContain("<NewtonsoftJsonVersion>11.0.1</NewtonsoftJsonVersion>", content);
        Assert.DoesNotContain("<MicrosoftExtensionsVersion>2.1.0</MicrosoftExtensionsVersion>", content);
    }

    [Fact]
    public static void Can_Upgrade_CPVM_With_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("cpvm-variables");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var propsFilePath = Path.Combine(project.Path, "Directory.Packages.props");
        var content = File.ReadAllText(propsFilePath);

        // Verify variable references are preserved in Directory.Packages.props
        Assert.Contains("Version=\"$(NewtonsoftJsonVersion)\"", content);
        Assert.Contains("Version=\"$(MicrosoftExtensionsVersion)\"", content);

        // Verify old versions are not present
        Assert.DoesNotContain("<NewtonsoftJsonVersion>11.0.1</NewtonsoftJsonVersion>", content);
        Assert.DoesNotContain("<MicrosoftExtensionsVersion>2.1.0</MicrosoftExtensionsVersion>", content);
    }

    [Fact]
    public static void Can_Upgrade_Cross_File_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("cross-file-variables");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        // Variables are defined in Directory.Build.props
        var propsFilePath = Path.Combine(project.Path, "Directory.Build.props");
        var propsContent = File.ReadAllText(propsFilePath);

        // But used in the project file
        var projectFilePath = Directory.GetFiles(project.Path, "*.csproj").First();
        var projectContent = File.ReadAllText(projectFilePath);

        // Verify variable references are preserved in project file
        Assert.Contains("Version=\"$(NewtonsoftJsonVersion)\"", projectContent);
        Assert.Contains("Version=\"$(MicrosoftExtensionsVersion)\"", projectContent);

        // Verify old versions are not present in Directory.Build.props
        Assert.DoesNotContain("<NewtonsoftJsonVersion>11.0.1</NewtonsoftJsonVersion>", propsContent);
        Assert.DoesNotContain("<MicrosoftExtensionsVersion>2.1.0</MicrosoftExtensionsVersion>", propsContent);
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

    public sealed class RequiresFileBasedAppSdkAttribute : FactAttribute
    {
        private static readonly Version MinimumSdkVersion = new(10, 0, 300);

        public RequiresFileBasedAppSdkAttribute()
        {
            if (!DotNetSdkDetector.HasSdkVersionAtLeast(MinimumSdkVersion))
            {
                Skip = $"Requires .NET SDK {MinimumSdkVersion} or newer.";
            }
        }
    }

    internal static class DotNetSdkDetector
    {
        public static bool HasSdkVersionAtLeast(Version minimumVersion)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks")
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });

                if (process is null)
                {
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();

                if (!process.WaitForExit(milliseconds: 10_000))
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    return false;
                }

                return output
                    .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                    .Where(sdkVersion => !string.IsNullOrWhiteSpace(sdkVersion))
                    .Select(sdkVersion => sdkVersion!.Split('-')[0])
                    .Any(sdkVersion => Version.TryParse(sdkVersion, out var version) && version.CompareTo(minimumVersion) >= 0);
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }
}
