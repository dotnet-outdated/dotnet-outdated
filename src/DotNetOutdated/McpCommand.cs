using DotNetOutdated.Core.Services;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO.Abstractions;
using System.Threading.Tasks;

namespace DotNetOutdated
{
    [Command(Name = "mcp", Description = "Runs the Model Context Protocol (MCP) server.")]
    internal class McpCommand
    {
        public async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            // Build a new service provider with McpConsole to redirect output to stderr
            var services = new ServiceCollection()
                 .AddSingleton<IConsole>(McpConsole.Singleton)
                 .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                 .AddSingleton<IFileSystem, FileSystem>()
                 .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                 .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                 .AddSingleton<IDotNetRunner, DotNetRunner>()
                 .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                 .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                 .AddSingleton<IDotNetPackageService, DotNetPackageService>()
                 .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                 .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                 .BuildServiceProvider();

            var server = new McpServer(
                services,
                services.GetRequiredService<IProjectDiscoveryService>(),
                services.GetRequiredService<IProjectAnalysisService>(),
                services.GetRequiredService<IDotNetPackageService>(),
                services.GetRequiredService<INuGetPackageResolutionService>(),
                Console.OpenStandardInput(),
                Console.OpenStandardOutput()
            );

            await server.RunAsync();

            return 0;
        }
    }
}
