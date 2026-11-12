using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class McpServerTests
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IProjectDiscoveryService _projectDiscoveryService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private IReferenceUpgrader _referenceUpgrader;
        private readonly IOutdatedDependencyService _outdatedDependencyService;

        public McpServerTests()
        {
            _serviceProvider = Substitute.For<IServiceProvider>();
            _projectDiscoveryService = Substitute.For<IProjectDiscoveryService>();
            _projectAnalysisService = Substitute.For<IProjectAnalysisService>();
            _referenceUpgrader = Substitute.For<IReferenceUpgrader>();
            _outdatedDependencyService = Substitute.For<IOutdatedDependencyService>();
        }

        private async Task<string> RunServerAsync(string input)
        {
            var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var outputStream = new MemoryStream();

            var server = new McpServer(
                _serviceProvider,
                _projectDiscoveryService,
                _projectAnalysisService,
                _referenceUpgrader,
                _outdatedDependencyService,
                inputStream,
                outputStream
            );

            await server.RunAsync();

            outputStream.Position = 0;
            using var reader = new StreamReader(outputStream);
            return await reader.ReadToEndAsync();
        }

        [Fact]
        public async Task Initialize_ReturnsCorrectCapabilities()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"id\": 1}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("\"result\"", output);
            Assert.Contains("\"protocolVersion\":\"2025-06-18\"", output);
            Assert.Contains("\"dotnet-outdated\"", output);
        }

        [Fact]
        public async Task Initialize_EchoesRequestedLegacyProtocolVersion()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"params\": { \"protocolVersion\": \"2024-11-05\" }, \"id\": 1}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("\"protocolVersion\":\"2024-11-05\"", output);
        }

        [Fact]
        public async Task ServerDiscover_ReturnsSupportedVersionsAndCapabilities()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"server/discover\", \"params\": { \"_meta\": { \"io.modelcontextprotocol/protocolVersion\": \"2026-07-28\" } }, \"id\": 1}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("\"resultType\":\"complete\"", output);
            Assert.Contains("\"supportedVersions\":[\"2026-07-28\"]", output);
            Assert.Contains("\"tools\":{}", output);
            Assert.Contains("io.modelcontextprotocol/serverInfo", output);
            Assert.Contains("\"dotnet-outdated\"", output);
        }

        [Fact]
        public async Task UnsupportedProtocolVersion_ReturnsError()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/list\", \"params\": { \"_meta\": { \"io.modelcontextprotocol/protocolVersion\": \"1900-01-01\" } }, \"id\": 1}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("\"error\"", output);
            Assert.Contains("-32022", output);
            Assert.Contains("\"supported\":[\"2026-07-28\"]", output);
            Assert.Contains("\"requested\":\"1900-01-01\"", output);
        }

        [Fact]
        public async Task ToolsList_ReturnsAvailableTools()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/list\", \"id\": 2}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("discover_projects", output);
            Assert.Contains("analyze_project", output);
            Assert.Contains("update_package", output);
        }

        [Fact]
        public async Task ToolsList_ModernRequest_IncludesCachingAndResultType()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/list\", \"params\": { \"_meta\": { \"io.modelcontextprotocol/protocolVersion\": \"2026-07-28\" } }, \"id\": 2}\n";

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("\"resultType\":\"complete\"", output);
            Assert.Contains("\"ttlMs\":3600000", output);
            Assert.Contains("\"cacheScope\":\"public\"", output);
            Assert.Contains("io.modelcontextprotocol/serverInfo", output);
        }

        [Fact]
        public async Task DiscoverProjects_ReturnsProjects()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"discover_projects\", \"arguments\": { \"path\": \"/test\" } }, \"id\": 3}\n";

            var projects = new List<string> { "/test/project1.csproj" };
            _projectDiscoveryService.DiscoverProjects("/test", false).Returns(projects);

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("project1.csproj", output);
            Assert.Contains("\"resultType\":\"complete\"", output);
        }

        [Fact]
        public async Task AnalyzeProject_ReportsEveryDependencyWithItsLatestVersion()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"analyze_project\", \"arguments\": { \"projectPath\": \"/test/p.csproj\" } }, \"id\": 3}\n";
            var dependency = new Dependency { Name = "Contoso.Widgets", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
            var projects = new List<Project>
            {
                new()
                {
                    Name = "p",
                    FilePath = "/test/p.csproj",
                    Version = new NuGetVersion("1.0.0"),
                    TargetFrameworks = [new TargetFramework { Name = NuGetFramework.Parse("net9.0"), Dependencies = [dependency] }],
                },
            };
            _projectAnalysisService.AnalyzeProjectAsync("/test/p.csproj", Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>()).Returns(projects);
            _outdatedDependencyService
                .FindOutdatedAsync(projects, Arg.Is<OutdatedOptions>(o => o.IncludeUpToDate && o.IncludeAutoReferences), Arg.Any<CancellationToken>())
                .Returns([new OutdatedProject
                {
                    Name = "p",
                    FilePath = "/test/p.csproj",
                    TargetFrameworks = [new OutdatedTargetFramework
                    {
                        Name = NuGetFramework.Parse("net9.0"),
                        Dependencies = [new OutdatedDependency { Dependency = dependency, LatestVersion = new NuGetVersion("1.2.0") }],
                    }],
                }]);

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("Contoso.Widgets", output);
            Assert.Contains("1.2.0", output);
            Assert.Contains("Minor", output);
        }

        [Fact]
        public async Task AnalyzeProject_ReportsAProjectWithNoDependencies()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"analyze_project\", \"arguments\": { \"projectPath\": \"/test/App.csproj\" } }, \"id\": 3}\n";
            var projects = new List<Project>
            {
                new()
                {
                    Name = "App",
                    FilePath = "/test/App.csproj",
                    Version = new NuGetVersion("1.0.0"),
                    TargetFrameworks = [new TargetFramework { Name = NuGetFramework.Parse("net8.0") }],
                },
            };
            _projectAnalysisService.AnalyzeProjectAsync("/test/App.csproj", Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>()).Returns(projects);
            _outdatedDependencyService
                .FindOutdatedAsync(projects, Arg.Any<OutdatedOptions>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<OutdatedProject>());

            var analyzed = ParseAnalyzeProjectResult(await RunServerAsync(input));

            var project = Assert.Single(analyzed.EnumerateArray());
            Assert.Equal("App", project.GetProperty("name").GetString());
            Assert.Equal("/test/App.csproj", project.GetProperty("filePath").GetString());
            var targetFramework = Assert.Single(project.GetProperty("targetFrameworks").EnumerateArray());
            Assert.Equal("net8.0", targetFramework.GetProperty("name").GetString());
            Assert.Empty(targetFramework.GetProperty("dependencies").EnumerateArray());
        }

        [Fact]
        public async Task AnalyzeProject_ReportsATargetFrameworkWithNoDependencies()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"analyze_project\", \"arguments\": { \"projectPath\": \"/test/p.csproj\" } }, \"id\": 3}\n";
            var dependency = new Dependency { Name = "Contoso.Widgets", VersionRange = VersionRange.Parse("1.0.0"), ResolvedVersion = new NuGetVersion("1.0.0") };
            var net8 = NuGetFramework.Parse("net8.0");
            var net9 = NuGetFramework.Parse("net9.0");
            var projects = new List<Project>
            {
                new()
                {
                    Name = "p",
                    FilePath = "/test/p.csproj",
                    Version = new NuGetVersion("1.0.0"),
                    TargetFrameworks =
                    [
                        new TargetFramework { Name = net8 },
                        new TargetFramework { Name = net9, Dependencies = [dependency] },
                    ],
                },
            };
            _projectAnalysisService.AnalyzeProjectAsync("/test/p.csproj", Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>()).Returns(projects);
            _outdatedDependencyService
                .FindOutdatedAsync(projects, Arg.Any<OutdatedOptions>(), Arg.Any<CancellationToken>())
                .Returns([new OutdatedProject
                {
                    Name = "p",
                    FilePath = "/test/p.csproj",
                    TargetFrameworks = [new OutdatedTargetFramework
                    {
                        Name = net9,
                        Dependencies = [new OutdatedDependency { Dependency = dependency, LatestVersion = new NuGetVersion("1.2.0") }],
                    }],
                }]);

            var analyzed = ParseAnalyzeProjectResult(await RunServerAsync(input));

            var project = Assert.Single(analyzed.EnumerateArray());
            var targetFrameworks = project.GetProperty("targetFrameworks").EnumerateArray().ToList();
            Assert.Equal(2, targetFrameworks.Count);
            Assert.Equal("net8.0", targetFrameworks[0].GetProperty("name").GetString());
            Assert.Empty(targetFrameworks[0].GetProperty("dependencies").EnumerateArray());
            Assert.Equal("net9.0", targetFrameworks[1].GetProperty("name").GetString());
            var reported = Assert.Single(targetFrameworks[1].GetProperty("dependencies").EnumerateArray());
            Assert.Equal("Contoso.Widgets", reported.GetProperty("name").GetString());
            Assert.Equal("1.2.0", reported.GetProperty("latestVersion").GetString());
        }

        [Fact]
        public async Task AnalyzeProject_NamesEachFrameworkByTheStringUpdatePackageAccepts()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"analyze_project\", \"arguments\": { \"projectPath\": \"/test/p.csproj\" } }, \"id\": 3}\n";
            var projects = new List<Project>
            {
                new()
                {
                    Name = "p",
                    FilePath = "/test/p.csproj",
                    Version = new NuGetVersion("1.0.0"),
                    TargetFrameworks =
                    [
                        new TargetFramework { Name = NuGetFramework.Parse("net9.0") },
                        new TargetFramework { Name = NuGetFramework.Parse("net8.0-windows7.0"), TargetAlias = "net8.0-windows" },
                    ],
                },
            };
            _projectAnalysisService.AnalyzeProjectAsync("/test/p.csproj", Arg.Any<ProjectAnalysisOptions>(), Arg.Any<CancellationToken>()).Returns(projects);
            _outdatedDependencyService
                .FindOutdatedAsync(projects, Arg.Any<OutdatedOptions>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<OutdatedProject>());

            var analyzed = ParseAnalyzeProjectResult(await RunServerAsync(input));

            var names = analyzed[0].GetProperty("targetFrameworks").EnumerateArray()
                .Select(tf => tf.GetProperty("name"))
                .ToList();
            Assert.All(names, name => Assert.Equal(JsonValueKind.String, name.ValueKind));
            Assert.Equal(["net9.0", "net8.0-windows"], names.Select(name => name.GetString()));
        }

        private static JsonElement ParseAnalyzeProjectResult(string output) =>
            JsonDocument.Parse(ToolResultText(output)).RootElement.Clone();

        private static string ToolResultText(string output)
        {
            using var response = JsonDocument.Parse(output);
            return response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        }

        [Theory]
        [InlineData("(,14.0.0)")]
        [InlineData("*")]
        [InlineData("13.*")]
        [InlineData("[13.0.1, 14.0.0)")]
        public async Task UpdatePackage_RejectsVersionRangesAndFloatingVersions(string version)
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                $"{{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"{version}\", \"framework\": \"net8.0\" }} }}, \"id\": 4}}\n";

            var output = await RunServerAsync(input);

            Assert.Contains("Invalid version", output);
            _referenceUpgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
        }

        [Fact]
        public async Task UpdatePackage_PassesExactVersion()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"13.0.1\", \"framework\": \"net8.0\" } }, \"id\": 5}\n";
            UpgradeReturns(UpgradeOutcome.Upgraded);

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Newtonsoft.Json to 13.0.1", output);
            _referenceUpgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade =>
                upgrade.Name == "Newtonsoft.Json" && upgrade.Version == new NuGetVersion("13.0.1") &&
                upgrade.Projects.Single() == new UpgradeProject("/test/p.csproj", "net8.0")));
        }

        [Theory]
        [InlineData(UpgradeOutcome.Upgraded, "Successfully updated Newtonsoft.Json to 13.0.1")]
        [InlineData(UpgradeOutcome.AlreadyCurrent, "Newtonsoft.Json is already at 13.0.1")]
        public async Task UpdatePackage_ReportsTheOutcomeAndWarnings(UpgradeOutcome outcome, string expected)
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"13.0.1\", \"framework\": \"net8.0\" } }, \"id\": 5}\n";
            UpgradeReturns(outcome, warnings: ["a version expression was replaced"]);

            var output = await RunServerAsync(input);

            var text = ToolResultText(output);
            Assert.StartsWith(expected, text);
            Assert.Contains("Warning: a version expression was replaced", text);
            Assert.DoesNotContain("\"isError\":true", output);
        }

        [Fact]
        public async Task UpdatePackage_KeepsTheUpperBoundOfADeclaredRange()
        {
            // The same range policy as the CLI: [6.0.13, 7.0.0) upgraded to 6.0.36 stays below 7.0.0.
            var projectPath = XFS.Path(@"c:\repo\App\App.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><PackageReference Include=""Foo"" Version=""[6.0.13, 7.0.0)"" /></ItemGroup></Project>"),
            });
            var runner = Substitute.For<IDotNetRunner>();
            runner.Run(default, default!).ReturnsForAnyArgs(new RunStatus("", "", 0));
            _referenceUpgrader = new ReferenceUpgrader(runner, fileSystem);
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                $"{{ \"projectPath\": {JsonSerializer.Serialize(projectPath)}, \"packageName\": \"Foo\", \"version\": \"6.0.36\", \"framework\": \"net8.0\" }} }}, \"id\": 5}}\n";

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Foo to 6.0.36", output);
            runner.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(args => args[0] == "add" && args[5] == "[6.0.36, 7.0.0)"));
        }

        [Fact]
        public async Task UpdatePackage_WithoutKind_LeavesTheKindToBeDetected()
        {
            // Clients written before "kind" existed omit it, so the SDK has to be recognised by name (#827).
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Aspire.AppHost.Sdk\", \"version\": \"9.1.0\", \"framework\": \"net8.0\" } }, \"id\": 6}\n";
            UpgradeReturns(UpgradeOutcome.Upgraded);

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Aspire.AppHost.Sdk to 9.1.0", output);
            _referenceUpgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Name == "Aspire.AppHost.Sdk" && upgrade.Kind == null));
        }

        [Fact]
        public async Task UpdatePackage_WithSdkKind_UpdatesTheSdk()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Aspire.AppHost.Sdk\", \"version\": \"9.1.0\", \"framework\": \"net8.0\", \"kind\": \"sdk\" } }, \"id\": 6}\n";
            UpgradeReturns(UpgradeOutcome.Upgraded);

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Aspire.AppHost.Sdk to 9.1.0", output);
            _referenceUpgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Name == "Aspire.AppHost.Sdk" && upgrade.Kind == ReferenceKind.Sdk));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("\"\"")]
        [InlineData("\"nuget\"")]
        public async Task UpdatePackage_WithInvalidKind_ReturnsErrorWithoutUpdating(string kindJson)
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"13.0.1\", \"framework\": \"net8.0\", \"kind\": " + kindJson + " } }, \"id\": 7}\n";

            var output = await RunServerAsync(input);

            Assert.Contains("Invalid kind", output);
            _referenceUpgrader.DidNotReceiveWithAnyArgs().Upgrade(default!);
        }

        [Fact]
        public async Task UpdatePackage_WithNullKind_LeavesTheKindToBeDetected()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"13.0.1\", \"framework\": \"net8.0\", \"kind\": null } }, \"id\": 8}\n";
            UpgradeReturns(UpgradeOutcome.Upgraded);

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Newtonsoft.Json to 13.0.1", output);
            _referenceUpgrader.Received(1).Upgrade(Arg.Is<ReferenceUpgrade>(upgrade => upgrade.Kind == null));
        }

        [Fact]
        public async Task UpdatePackage_WhenUpgradeFails_ReturnsOutputAndErrors()
        {
            // Arrange
            // dotnet add package reports NuGet failures on stdout, so the failure details include it.
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": { \"projectPath\": \"/test/project1.csproj\", \"packageName\": \"Contoso.Widgets\", \"version\": \"2.0.0\", \"framework\": \"net8.0\" } }, \"id\": 4}\n";

            UpgradeReturns(UpgradeOutcome.Failed, "error: Cannot edit items in imported files\nstderr detail");

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("Failed to update package: error: Cannot edit items in imported files", output);
            Assert.Contains("stderr detail", output);
            Assert.Contains("\"isError\":true", output);
        }

        private void UpgradeReturns(UpgradeOutcome outcome, string failureDetails = "", string[] warnings = null) =>
            _referenceUpgrader.Upgrade(Arg.Any<ReferenceUpgrade>()).Returns(call => call.Arg<ReferenceUpgrade>().Projects
                .Select(project => new ProjectUpgradeResult
                {
                    ProjectPath = project.Path,
                    Outcome = outcome,
                    FailureDetails = failureDetails,
                    Warnings = warnings ?? [],
                })
                .ToList());
    }
}
