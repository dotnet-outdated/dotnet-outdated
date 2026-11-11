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
            dependency => dependency.GetProperty("Name").GetString() == "DotNetOutdated.Fake.Json");

        Assert.Equal("11.0.1", analyzedDependency.GetProperty("ResolvedVersion").GetString());
        Assert.Equal("13.0.4", analyzedDependency.GetProperty("LatestVersion").GetString());
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("file-based-app");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:property FakeJsonPackageId=DotNetOutdated.Fake.Json", content);
        Assert.Contains("#:property FakeJsonPackageVersion=13.0.4", content);
        Assert.Contains("#:package $(FakeJsonPackageId)@$(FakeJsonPackageVersion)", content);
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Direct_Package_Directive()
    {
        using var project = TestSetup("file-based-app-direct-package");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:package DotNetOutdated.Fake.Json@13.0.4", content);
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

        Assert.Contains(dependencies, dependency => dependency.GetProperty("Name").GetString() == "DotNetOutdated.Fake.App.Sdk");
        Assert.DoesNotContain(dependencies, dependency => dependency.GetProperty("Name").GetString() == "DotNetOutdated.Fake.App.Generator");

        var analyzedSdk = Assert.Single(dependencies, dependency => dependency.GetProperty("Name").GetString() == "DotNetOutdated.Fake.App.Sdk");

        Assert.Equal("6.0.0", analyzedSdk.GetProperty("ResolvedVersion").GetString());
        Assert.Equal("6.1.0", analyzedSdk.GetProperty("LatestVersion").GetString());
    }

    [RequiresFileBasedAppSdk]
    public static void Can_Upgrade_File_Based_App_With_Sdk_Directive()
    {
        using var project = TestSetup("file-based-app-sdk");

        var appPath = Path.Combine(project.Path, "app.cs");

        var actual = Program.Main([appPath, "--upgrade", "--no-restore"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(appPath);

        Assert.Contains("#:sdk DotNetOutdated.Fake.App.Sdk@6.1.0", content);
        Assert.DoesNotContain("DotNetOutdated.Fake.App.Generator", content);
    }

    [RequiresFileBasedAppSdk]
    public static void File_Based_App_With_Outdated_Package_Fails_On_Updates()
    {
        using var project = TestSetup("file-based-app");

        var actual = Program.Main([Path.Combine(project.Path, "app.cs"), "--fail-on-updates"]);

        Assert.Equal(2, actual);
    }

    [RequiresFileBasedAppSdk]
    public static void File_Based_App_With_Outdated_Sdk_Fails_On_Updates()
    {
        using var project = TestSetup("file-based-app-sdk");

        var actual = Program.Main([Path.Combine(project.Path, "app.cs"), "--fail-on-updates"]);

        Assert.Equal(2, actual);
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

        Assert.Contains("DotNetOutdated.Fake.Json", dependencyNames);
        Assert.Equal(includeAutoReferences, dependencyNames.Contains("DotNetOutdated.Fake.TestFramework"));
        Assert.Equal(includeAutoReferences, dependencyNames.Contains("DotNetOutdated.Fake.TestHost"));
    }

    [Fact]
    public static void Can_Upgrade_Project_With_Packages_Injected_By_Custom_Sdk()
    {
        using var project = TestSetup("sdk-injected-packages");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(Path.Combine(project.Path, "sdk-injected-packages.csproj"));

        Assert.Contains("<PackageReference Include=\"DotNetOutdated.Fake.Json\" Version=\"13.0.4\" />", content);
        Assert.DoesNotContain("DotNetOutdated.Fake.TestFramework", content);
        Assert.DoesNotContain("DotNetOutdated.Fake.TestHost", content);
    }

    [Theory]
    [InlineData("project-sdk-attribute", "<Project Sdk=\"DotNetOutdated.Fake.NoTargets.Sdk/3.7.0\">")]
    [InlineData("project-sdk-element", "<Sdk Name=\"DotNetOutdated.Fake.NoTargets.Sdk\" Version=\"3.7.0\" />")]
    [InlineData("project-sdk-import", "Sdk=\"DotNetOutdated.Fake.NoTargets.Sdk\" Version=\"3.7.0\"")]
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
        Assert.Equal("DotNetOutdated.Fake.NoTargets.Sdk", sdk.Name);
        Assert.Equal(new NuGetVersion("3.8.0"), sdk.ResolvedVersion);
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
                    Assert.Equal("8.0.1", (string)dependency["LatestVersion"]);
                }
            }
        }

        var csproj = XDocument.Load(Path.Combine(directory.Path, "Project.csproj"));
        var csprojDependencyVersions = csproj.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        Assert.Equal("8.0.1", csprojDependencyVersions["DotNetOutdated.Fake.TextJson"]);
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
        Assert.DoesNotContain("DotNetOutdated.Fake.TextEncodings", latestVersions.Keys);
        Assert.DoesNotContain("DotNetOutdated.Fake.AsyncInterfaces", latestVersions.Keys);

        // A declared upper bound below the maximum version wins: [6.0.0, 7.0.0)
        Assert.Equal(new NuGetVersion("6.0.1"), latestVersions["DotNetOutdated.Fake.Options"]);

        // The maximum version wins over a higher declared upper bound: [6.0.0, 10.0.0)
        Assert.Equal(new NuGetVersion("8.0.1"), latestVersions["DotNetOutdated.Fake.Logging"]);

        // An open-ended version is capped at the maximum version: 6.0.0
        Assert.Equal(new NuGetVersion("8.0.1"), latestVersions["DotNetOutdated.Fake.TextJson"]);
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
        Assert.Equal("[6.0.0]", versions["DotNetOutdated.Fake.TextEncodings"]);
        Assert.Equal("[9.0.0]", versions["DotNetOutdated.Fake.AsyncInterfaces"]);

        // Declared upper bounds are kept; --maximum-version only narrows the choice
        Assert.Equal("[6.0.1, 7.0.0)", versions["DotNetOutdated.Fake.Options"]);

        Assert.Equal("[8.0.1, 10.0.0)", versions["DotNetOutdated.Fake.Logging"]);

        // A plain version stays a plain version
        Assert.Equal("8.0.1", versions["DotNetOutdated.Fake.TextJson"]);
    }

    [Fact]
    public static void Can_Upgrade_Direct_Reference_With_Variables_Preserves_Variable_References()
    {
        using var project = TestSetup("direct-reference-variables");

        var actual = Program.Main([project.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var projectFilePath = Directory.GetFiles(project.Path, "*.csproj").First();
        var content = File.ReadAllText(projectFilePath);

        Assert.Contains("Version=\"$(FakeJsonVersion)\"", content);
        Assert.Contains("Version=\"$(FakeExtensionsVersion)\"", content);

        Assert.Contains("<FakeJsonVersion>13.0.4</FakeJsonVersion>", content);
        Assert.Contains("<FakeExtensionsVersion>10.0.0</FakeExtensionsVersion>", content);
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
            "\t\t<PackageVersion Include=\"DotNetOutdated.Fake.Json\" Version=\"13.0.1\" />\n" +
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
        Assert.Contains("Version=\"$(FakeJsonVersion)\"", content);
        Assert.Contains("Version=\"$(FakeExtensionsVersion)\"", content);

        // The variables hold the new versions
        Assert.Contains("<FakeJsonVersion>13.0.4</FakeJsonVersion>", content);
        Assert.Contains("<FakeExtensionsVersion>10.0.0</FakeExtensionsVersion>", content);
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
        Assert.Contains("Version=\"$(FakeJsonVersion)\"", projectContent);
        Assert.Contains("Version=\"$(FakeExtensionsVersion)\"", projectContent);

        // The variables in Directory.Build.props hold the new versions
        Assert.Contains("<FakeJsonVersion>13.0.4</FakeJsonVersion>", propsContent);
        Assert.Contains("<FakeExtensionsVersion>10.0.0</FakeExtensionsVersion>", propsContent);
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

        Assert.Equal("7.0.0", jsonDependencyVersions["DotNetOutdated.Fake.TextJson"]);
        Assert.Equal("7.0.0", jsonDependencyVersions["DotNetOutdated.Fake.DependencyInjection"]);

        var csproj = XDocument.Load(Path.Combine(directory.Path, "version-range.csproj"));
        var csprojDependencyVersions = csproj.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version"));

        Assert.Equal("[7.0.0, 8.0.0)", csprojDependencyVersions["DotNetOutdated.Fake.TextJson"]);
        Assert.Equal("[7.0.0]", csprojDependencyVersions["DotNetOutdated.Fake.DependencyInjection"]);
    }

    [Fact]
    public static void Recursive_Reports_Each_Project_Once_When_A_Project_References_Another()
    {
        // B references A, so the restore graph of B holds A as well as B (issue #830).
        using var directory = TestSetup("recursive-solution");
        var outputPath = Path.Combine(directory.Path, "output.json");

        var actual = Program.Main([directory.Path, "--recursive", "--output", outputPath, "--output-format:json"]);
        Assert.Equal(0, actual);

        using var output = JsonDocument.Parse(File.ReadAllText(outputPath));
        var reportedPaths = output.RootElement.GetProperty("Projects").EnumerateArray()
            .Select(project => project.GetProperty("FilePath").GetString())
            .Order(StringComparer.Ordinal)
            .ToList();

        // Compare file names: which of the equivalent paths is reported (e.g. /var or /private/var on macOS) is not fixed.
        Assert.Equal(["A.csproj", "B.csproj"], reportedPaths.Select(Path.GetFileName));
    }

    [Fact]
    public static void Recursive_Reports_Each_Project_Once_When_Reached_Through_A_Symbolic_Link()
    {
        // The project is analyzed through the link, but MSBuild reports a referenced project by its real path (issue #830).
        using var directory = TestSetup("recursive-solution");
        var linkPath = directory.Path + "-link";
        Directory.CreateSymbolicLink(linkPath, directory.Path);

        try
        {
            var outputPath = Path.Combine(directory.Path, "output.json");

            var actual = Program.Main([linkPath, "--recursive", "--output", outputPath, "--output-format:json"]);
            Assert.Equal(0, actual);

            using var output = JsonDocument.Parse(File.ReadAllText(outputPath));
            var reportedNames = output.RootElement.GetProperty("Projects").EnumerateArray()
                .Select(project => Path.GetFileName(project.GetProperty("FilePath").GetString()))
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["A.csproj", "B.csproj"], reportedNames);
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    [Fact]
    public static void Can_Upgrade_Version_Range_Held_In_Variable()
    {
        using var directory = TestSetup("version-range-variables");

        var actual = Program.Main([directory.Path, "--upgrade"]);
        Assert.Equal(0, actual);

        var content = File.ReadAllText(Path.Combine(directory.Path, "version-range-variables.csproj"));
        Assert.Contains("Version=\"$(FakeJsonVersion)\"", content);
        Assert.Contains("<FakeJsonVersion>[12.0.3, 13.0.0)</FakeJsonVersion>", content);
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

        Assert.Equal("$(FakeJsonVersion)", packageVersions["DotNetOutdated.Fake.Json"]);
        Assert.Equal("[12.0.3, 13.0.0)", (string)props.Descendants("FakeJsonVersion").Single());

        Assert.Equal("[6.0.1, 7.0.0)", packageVersions["DotNetOutdated.Fake.TextJson"]);
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

        foreach (var source in Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(temp.Path, Path.GetRelativePath(projectPath, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Path.GetFileName(source) == "packages.lock.json")
            {
                File.WriteAllText(destination, FillContentHashes(File.ReadAllText(source)));
            }
            else
            {
                File.Copy(source, destination);
            }
        }

        // Packages come from the local fake feed, so results don't change when packages are published upstream.
        File.WriteAllText(Path.Combine(temp.Path, "NuGet.Config"), TestPackageFeed.NuGetConfig);

        return temp;
    }

    // Lock files in test-projects hold "{{hash:Id/Version}}" in place of the content hash of a fake package.
    private static string FillContentHashes(string lockFile) =>
        System.Text.RegularExpressions.Regex.Replace(
            lockFile,
            @"\{\{hash:(?<id>[^/]+)/(?<version>[^}]+)\}\}",
            match => TestPackageFeed.ContentHash(match.Groups["id"].Value, match.Groups["version"].Value));

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
