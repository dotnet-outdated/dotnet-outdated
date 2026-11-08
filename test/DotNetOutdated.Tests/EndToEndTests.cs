using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

/// <summary>
/// Skips the test when the installed .NET SDK is older than the minimum required for file-based apps.
/// Inherits <see cref="FactAttribute"/> so xUnit discovers and runs the test method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
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
                process.WaitForExit(milliseconds: 5_000);
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

    [RequiresFileBasedAppSdk]
    public static void Can_Analyze_File_Based_App_With_Sdk_Directive()
    {
        using var project = TestSetup("file-based-app-sdk");

        var appPath = Path.Combine(project.Path, "app.cs");
        var outputPath = Path.Combine(project.Path, "output.json");

        var actual = Program.Main([appPath, "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));
        var analyzedProject = Assert.Single(output.RootElement.GetProperty("Projects").EnumerateArray());

        Assert.Equal("app.cs", analyzedProject.GetProperty("Name").GetString());
        Assert.Equal(Path.GetFullPath(appPath), analyzedProject.GetProperty("FilePath").GetString());

        var dependencies = analyzedProject
            .GetProperty("TargetFrameworks")
            .EnumerateArray()
            .SelectMany(targetFramework => targetFramework.GetProperty("Dependencies").EnumerateArray())
            .ToList();

        Assert.Contains(dependencies, dependency => dependency.GetProperty("Name").GetString() == "Cake.Sdk");
        Assert.DoesNotContain(dependencies, dependency => dependency.GetProperty("Name").GetString() == "Cake.Generator");

        var analyzedSdk = Assert.Single(dependencies, dependency => dependency.GetProperty("Name").GetString() == "Cake.Sdk");

        Assert.Equal("6.0.0", analyzedSdk.GetProperty("ResolvedVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(analyzedSdk.GetProperty("LatestVersion").GetString()));
        Assert.NotEqual("6.0.0", analyzedSdk.GetProperty("LatestVersion").GetString());
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Sdk_Directive()
    {
        using var project = TestSetup("file-based-app-sdk");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:sdk Cake.Sdk@", content);
        Assert.DoesNotContain("#:sdk Cake.Sdk@6.0.0", content);
        Assert.DoesNotContain("Cake.Generator", content);
    }

    [Theory]
    [InlineData("sdk-injected-packages", false)]
    [InlineData("sdk-injected-packages", true)]
    [InlineData("sdk-injected-packages-restore-path", false)]
    public static void Packages_Injected_By_Custom_Sdk_Are_Auto_Referenced(string testProjectName, bool includeAutoReferences)
    {
        using var project = TestSetup(testProjectName);

        var outputPath = Path.Combine(project.Path, "output.json");

        List<string> arguments = [project.Path, "--output", outputPath, "--output-format:json"];
        if (includeAutoReferences)
        {
            arguments.Add("--include-auto-references");
        }

        var actual = Program.Main([.. arguments]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));
        var dependencyNames = output.RootElement
            .GetProperty("Projects").EnumerateArray()
            .SelectMany(p => p.GetProperty("TargetFrameworks").EnumerateArray())
            .SelectMany(tfm => tfm.GetProperty("Dependencies").EnumerateArray())
            .Select(d => d.GetProperty("Name").GetString())
            .ToList();

        Assert.Contains("Newtonsoft.Json", dependencyNames);
        Assert.Equal(includeAutoReferences, dependencyNames.Contains("MSTest.TestFramework"));
        Assert.Equal(includeAutoReferences, dependencyNames.Contains("Microsoft.NET.Test.Sdk"));
    }

    [Fact]
    public static void Can_Upgrade_Project_With_Packages_Injected_By_Custom_Sdk()
    {
        using var project = TestSetup("sdk-injected-packages");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(Path.Combine(project.Path, "sdk-injected-packages.csproj"));

        Assert.DoesNotContain("<PackageReference Include=\"Newtonsoft.Json\" Version=\"11.0.1\" />", content);
        Assert.DoesNotContain("MSTest.TestFramework", content);
        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", content);
    }

    [Theory]
    [InlineData("project-sdk-attribute", "<Project Sdk=\"Microsoft.Build.NoTargets/3.7.0\">")]
    [InlineData("project-sdk-element", "<Sdk Name=\"Microsoft.Build.NoTargets\" Version=\"3.7.0\" />")]
    [InlineData("project-sdk-import", "Sdk=\"Microsoft.Build.NoTargets\" Version=\"3.7.0\"")]
    public static void Can_Upgrade_Project_With_Versioned_Project_Sdk(string testProjectName, string originalSdkDeclaration)
    {
        using var project = TestSetup(testProjectName);

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var projectFilePath = Path.Combine(project.Path, $"{testProjectName}.csproj");
        var content = File.ReadAllText(projectFilePath);

        Assert.DoesNotContain(originalSdkDeclaration, content);
        Assert.DoesNotContain("PackageReference", content);

        var sdk = Assert.Single(ProjectSdkReferenceHelper.Discover(new System.IO.Abstractions.FileSystem(), projectFilePath));
        Assert.Equal("Microsoft.Build.NoTargets", sdk.Name);
        Assert.True(sdk.ResolvedVersion > new NuGetVersion("3.7.0"), $"Expected an upgraded SDK version, got {sdk.ResolvedVersion}");
    }

    [Theory]
    [InlineData("empty-solution", null, "Empty.sln")]
    [InlineData("empty-solution", "Empty.sln", "Empty.sln")]
    [InlineData("empty-slnx", "Empty.slnx", "Empty.slnx")]
    [InlineData("empty-solution-filter", "Empty.slnf", "Empty.slnf")]
    public static void Reports_Friendly_Message_For_Solution_With_No_Projects(string testProjectName, string inputFile, string reportedFile)
    {
        using var project = TestSetup(testProjectName);
        var input = inputFile is null ? project.Path : Path.Combine(project.Path, inputFile);

        var (exitCode, output) = RunAndCaptureOutput(input);

        Assert.Equal(1, exitCode);
        var expected = string.Format(
            CultureInfo.InvariantCulture,
            DotNetOutdated.Core.Resources.ValidationErrorMessages.NoRestorableProjectsInSolution,
            Path.Combine(project.Path, reportedFile));
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
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
    public static void Maximum_Version_Never_Widens_Declared_Version_Ranges()
    {
        using var directory = TestSetup("max-version-ranges");

        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, "--maximum-version:8.0", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));

        var latestVersions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in output.RootElement.GetProperty("Projects").EnumerateArray())
        {
            foreach (var tfm in project.GetProperty("TargetFrameworks").EnumerateArray())
            {
                foreach (var dependency in tfm.GetProperty("Dependencies").EnumerateArray())
                {
                    latestVersions[dependency.GetProperty("Name").GetString()] =
                        NuGetVersion.Parse(dependency.GetProperty("LatestVersion").GetString());
                }
            }
        }

        // Exact pins are never upgraded, whether they are below or above the maximum version
        Assert.DoesNotContain("System.Text.Encodings.Web", latestVersions.Keys);
        Assert.DoesNotContain("Microsoft.Bcl.AsyncInterfaces", latestVersions.Keys);

        // A declared upper bound below the maximum version wins: [6.0.0, 7.0.0)
        Assert.Equal(6, latestVersions["Microsoft.Extensions.Options"].Major);

        // The maximum version wins over a higher declared upper bound: [6.0.0, 10.0.0)
        Assert.Equal(8, latestVersions["Microsoft.Extensions.Logging"].Major);
        Assert.Equal(0, latestVersions["Microsoft.Extensions.Logging"].Minor);

        // An open-ended version is capped at the maximum version: 6.0.0
        Assert.Equal(8, latestVersions["System.Text.Json"].Major);
        Assert.Equal(0, latestVersions["System.Text.Json"].Minor);
    }

    [Fact]
    public static void Can_Upgrade_With_Maximum_Version_Preserving_Declared_Ranges()
    {
        using var directory = TestSetup("max-version-ranges");

        var actual = Program.Main([directory.Path, "--upgrade", "--maximum-version:8.0"]);
        Assert.Equal(0, actual);

        var csproj = XDocument.Load(Path.Combine(directory.Path, "Project.csproj"));
        var versions = csproj.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        // Exact pins are left alone
        Assert.Equal("[6.0.0]", versions["System.Text.Encodings.Web"]);
        Assert.Equal("[9.0.0]", versions["Microsoft.Bcl.AsyncInterfaces"]);

        // Declared upper bounds are kept; --maximum-version only narrows the choice
        var options = VersionRange.Parse(versions["Microsoft.Extensions.Options"]);
        Assert.Equal(6, options.MinVersion.Major);
        Assert.Equal(new NuGetVersion(7, 0, 0), options.MaxVersion);

        var logging = VersionRange.Parse(versions["Microsoft.Extensions.Logging"]);
        Assert.Equal(new NuGetVersion(8, 0, logging.MinVersion.Patch), logging.MinVersion);
        Assert.Equal(new NuGetVersion(10, 0, 0), logging.MaxVersion);

        // A plain version stays a plain version
        Assert.StartsWith("8.0.", versions["System.Text.Json"]);
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
    public static void Can_Upgrade_CPM_Preserves_Props_Whitespace()
    {
        using var project = TestSetup("central-package-management");
        var propsPath = Path.Combine(project.Path, "Directory.Packages.props");
        var original = "<Project>\n\n" +
            "\t<PropertyGroup>\n" +
            "\t\t<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n" +
            "\t</PropertyGroup>\n\n" +
            "\t<ItemGroup>\n" +
            "\t\t<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
            "\t</ItemGroup>\n\n" +
            "</Project>\n\n";
        File.WriteAllText(propsPath, original);

        var actual = Program.Main([project.Path, "--upgrade", "--maximum-version", "13.0.3"]);

        Assert.Equal(0, actual);
        Assert.Equal(original.Replace("13.0.1", "13.0.3"), File.ReadAllText(propsPath));
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

    [Fact]
    public static void Can_Upgrade_Version_Range_Held_In_Variable()
    {
        using var directory = TestSetup("version-range-variables");

        var actual = Program.Main([directory.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(Path.Combine(directory.Path, "version-range-variables.csproj"));
        Assert.Contains("Version=\"$(NewtonsoftJsonVersion)\"", content);
        Assert.Contains("<NewtonsoftJsonVersion>[12.0.3, 13.0.0)</NewtonsoftJsonVersion>", content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void Can_Upgrade_CPM_Version_Ranges(bool noRestore)
    {
        using var directory = TestSetup("version-range-cpm");

        List<string> args = [directory.Path, "--upgrade"];
        if (noRestore)
        {
            args.Add("--no-restore");
        }

        var actual = Program.Main([.. args]);
        Assert.Equal(0, actual);

        var props = XDocument.Load(Path.Combine(directory.Path, "Directory.Packages.props"));
        var packageVersions = props.Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        Assert.Equal("$(NewtonsoftJsonVersion)", packageVersions["Newtonsoft.Json"]);
        Assert.Equal("[12.0.3, 13.0.0)", (string)props.Descendants("NewtonsoftJsonVersion").Single());

        var systemTextJson = VersionRange.Parse(packageVersions["System.Text.Json"]);
        Assert.True(systemTextJson.MinVersion > new NuGetVersion(6, 0, 0));
        Assert.Equal(new NuGetVersion(7, 0, 0), systemTextJson.MaxVersion);
        Assert.False(systemTextJson.IsMaxInclusive);
    }

    private static (int ExitCode, string Output) RunAndCaptureOutput(string path)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = Program.Main([path]);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
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
