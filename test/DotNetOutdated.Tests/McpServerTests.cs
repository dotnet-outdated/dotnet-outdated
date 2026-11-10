using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class McpServerTests
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IProjectDiscoveryService _projectDiscoveryService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IDotNetPackageService _dotNetPackageService;
        private readonly INuGetPackageResolutionService _nugetService;

        public McpServerTests()
        {
            _serviceProvider = Substitute.For<IServiceProvider>();
            _projectDiscoveryService = Substitute.For<IProjectDiscoveryService>();
            _projectAnalysisService = Substitute.For<IProjectAnalysisService>();
            _dotNetPackageService = Substitute.For<IDotNetPackageService>();
            _nugetService = Substitute.For<INuGetPackageResolutionService>();
        }

        private async Task<string> RunServerAsync(string input)
        {
            var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var outputStream = new MemoryStream();

            var server = new McpServer(
                _serviceProvider,
                _projectDiscoveryService,
                _projectAnalysisService,
                _dotNetPackageService,
                _nugetService,
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
            _dotNetPackageService.ReceivedCalls().ToList().ForEach(call => Assert.NotEqual("AddPackage", call.GetMethodInfo().Name));
        }

        [Fact]
        public async Task UpdatePackage_PassesExactVersion()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Newtonsoft.Json\", \"version\": \"13.0.1\", \"framework\": \"net8.0\" } }, \"id\": 5}\n";
            _dotNetPackageService.AddPackage("/test/p.csproj", "Newtonsoft.Json", "net8.0", new VersionRange(new NuGetVersion("13.0.1")))
                .Returns(new RunStatus("", "", 0));

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Newtonsoft.Json to 13.0.1", output);
        }

        [Fact]
        public async Task UpdatePackage_WithSdkKind_UpdatesTheSdk()
        {
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": " +
                "{ \"projectPath\": \"/test/p.csproj\", \"packageName\": \"Aspire.AppHost.Sdk\", \"version\": \"9.1.0\", \"framework\": \"net8.0\", \"kind\": \"sdk\" } }, \"id\": 6}\n";
            _dotNetPackageService.AddPackage("/test/p.csproj", "Aspire.AppHost.Sdk", "net8.0", new VersionRange(new NuGetVersion("9.1.0")), kind: ReferenceKind.Sdk)
                .Returns(new RunStatus("", "", 0));

            var output = await RunServerAsync(input);

            Assert.Contains("Successfully updated Aspire.AppHost.Sdk to 9.1.0", output);
        }

        [Fact]
        public async Task UpdatePackage_WhenAddPackageFails_ReturnsOutputAndErrors()
        {
            // Arrange
            // dotnet add package reports NuGet failures on stdout, so the tool result must include it.
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"update_package\", \"arguments\": { \"projectPath\": \"/test/project1.csproj\", \"packageName\": \"Contoso.Widgets\", \"version\": \"2.0.0\", \"framework\": \"net8.0\" } }, \"id\": 4}\n";

            _dotNetPackageService
                .AddPackage("/test/project1.csproj", "Contoso.Widgets", "net8.0", new VersionRange(new NuGetVersion("2.0.0")))
                .Returns(new RunStatus("error: Cannot edit items in imported files", "stderr detail", 1));

            // Act
            var output = await RunServerAsync(input);

            // Assert
            Assert.Contains("Failed to update package: error: Cannot edit items in imported files", output);
            Assert.Contains("stderr detail", output);
            Assert.Contains("\"isError\":true", output);
        }
    }
}
