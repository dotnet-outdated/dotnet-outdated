using DotNetOutdated.Core.Services;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Abstractions;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class McpCommandDependencyInjectionTests
    {
        [Fact]
        public void McpCommand_ServiceProvider_CanResolveAllRequiredServices()
        {
            // Build the same service collection as McpCommand.OnExecuteAsync
            var services = new ServiceCollection()
                .AddSingleton<IConsole>(McpConsole.Singleton)
                .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                .AddSingleton<IFileSystem, FileSystem>()
                .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                .AddSingleton<IDotNetRunner, DotNetRunner>()
                .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                .AddSingleton<IVariableTrackingService>(provider =>
                    new VariableTrackingService(
                        provider.GetService<IFileSystem>(),
                        msg => provider.GetService<IReporter>().Warn(msg)))
                .AddSingleton<IDotNetPackageService, DotNetPackageService>()
                .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                .BuildServiceProvider();

            // Verify all services required by McpServer can be resolved
            Assert.NotNull(services.GetRequiredService<IProjectDiscoveryService>());
            Assert.NotNull(services.GetRequiredService<IProjectAnalysisService>());
            Assert.NotNull(services.GetRequiredService<IDotNetPackageService>());
            Assert.NotNull(services.GetRequiredService<INuGetPackageResolutionService>());
        }
    }
}
