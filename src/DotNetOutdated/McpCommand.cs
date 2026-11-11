using DotNetOutdated.Core.Services;
using DotNetOutdated.Services;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using System;
using System.Threading.Tasks;

namespace DotNetOutdated
{
    internal class McpCommand
    {
        public async Task<int> OnExecuteAsync()
        {
            // Build a new service provider with McpConsole to redirect output to stderr
            var services = new ServiceCollection()
                 .AddSingleton<IConsole>(McpConsole.Singleton)
                 .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                 .AddSingleton<IWarningReporter>(provider => provider.GetRequiredService<IReporter>())
                 .AddSingleton<ILogger>(provider => new ConsoleLogger(provider.GetService<IConsole>(), LogLevel.Warning))
                 .AddDotNetOutdated()
                 .BuildServiceProvider();

            var server = new McpServer(
                services,
                services.GetRequiredService<IProjectDiscoveryService>(),
                services.GetRequiredService<IProjectAnalysisService>(),
                services.GetRequiredService<IDotNetPackageService>(),
                services.GetRequiredService<IOutdatedDependencyService>(),
                Console.OpenStandardInput(),
                Console.OpenStandardOutput()
            );

            await server.RunAsync();

            return 0;
        }
    }
}
