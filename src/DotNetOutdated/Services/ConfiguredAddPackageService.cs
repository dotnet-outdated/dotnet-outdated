using DotNetOutdated.Core.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using System;
using System.IO.Abstractions;

namespace DotNetOutdated.Services
{
    /// <summary>
    /// A wrapper for the service to use. Since the program needs to be fully constructed before we
    /// can have access to the options.
    /// </summary>
    public class ConfiguredAddPackageService : IDotNetAddPackageService
    {
        private readonly Lazy<IDotNetAddPackageService> _configuredService;
        private readonly IServiceProvider _services;

        public ConfiguredAddPackageService(IServiceProvider serviceProvider)
        {
            _services = serviceProvider;
            _configuredService = new Lazy<IDotNetAddPackageService>(Init);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return _configuredService.Value.AddPackage(projectPath, packageName, frameworkName, version);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources)
        {
            return _configuredService.Value.AddPackage(projectPath, packageName, frameworkName, version, noRestore, ignoreFailedSources);
        }

        private IDotNetAddPackageService Init()
        {
            var app = _services.GetRequiredService<CommandLineApplication<Program>>();
            return !string.IsNullOrWhiteSpace(app.Model.DependencyFile)
                    ? new DependencyFileAddPackageService(_services.GetRequiredService<IFileSystem>(), app.Model.DependencyFile)
                    : _services.GetRequiredService<DotNetAddPackageService>();
        }
    }
}
