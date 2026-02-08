using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

        [Fact]
        public async Task Initialize_ReturnsCorrectCapabilities()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"id\": 1}\n";
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

            // Act
            await server.RunAsync();

            // Assert
            outputStream.Position = 0;
            using var reader = new StreamReader(outputStream);
            var output = await reader.ReadToEndAsync();

            Assert.Contains("\"result\"", output);
            Assert.Contains("\"protocolVersion\":\"2024-11-05\"", output);
            Assert.Contains("\"dotnet-outdated\"", output);
        }

        [Fact]
        public async Task ToolsList_ReturnsAvailableTools()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/list\", \"id\": 2}\n";
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

            // Act
            await server.RunAsync();

            // Assert
            outputStream.Position = 0;
            using var reader = new StreamReader(outputStream);
            var output = await reader.ReadToEndAsync();

            Assert.Contains("discover_projects", output);
            Assert.Contains("analyze_project", output);
            Assert.Contains("update_package", output);
        }

        [Fact]
        public async Task DiscoverProjects_ReturnsProjects()
        {
            // Arrange
            var input = "{\"jsonrpc\": \"2.0\", \"method\": \"tools/call\", \"params\": { \"name\": \"discover_projects\", \"arguments\": { \"path\": \"/test\" } }, \"id\": 3}\n";
            var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var outputStream = new MemoryStream();

            var projects = new List<string> { "/test/project1.csproj" };
            _projectDiscoveryService.DiscoverProjects("/test", false).Returns(projects);

            var server = new McpServer(
                _serviceProvider,
                _projectDiscoveryService,
                _projectAnalysisService,
                _dotNetPackageService,
                _nugetService,
                inputStream,
                outputStream
            );

            // Act
            await server.RunAsync();

            // Assert
            outputStream.Position = 0;
            using var reader = new StreamReader(outputStream);
            var output = await reader.ReadToEndAsync();

            Assert.Contains("project1.csproj", output);
        }
    }
}
