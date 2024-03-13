using Xunit;
using System;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Services;
using System.Reflection;
using System.Linq;
using NSubstitute;
using System.Collections.Generic;
using DotNetOutdated.Core.Models;
using NuGet.Versioning;
using NuGet.Frameworks;

namespace DotNetOutdated.Tests
{
    public class ConfiguredAddPackageServiceTests
    {
        private readonly Context _context = new();

        [Fact]
        public void ShouldCreateConfiguredService()
        {
            Assert.IsType<ConfiguredAddPackageService>(_context.PackageService);
        }

        [Fact]
        public void ShouldUseDefaultService()
        {
            _context.StartApp();
            Assert.IsType<DotNetAddPackageService>(_context.PackageServiceImplementation);
        }

        [Fact]
        public void ShouldUseDependenciesService()
        {
            _context.StartApp("/some.csproj", "-dpf", "some.props");
            Assert.IsType<DependencyFileAddPackageService>(_context.PackageServiceImplementation);
        }

        private sealed class Context : IDisposable
        {
            private readonly CommandLineApplication<Program> _app;
            private readonly Lazy<IServiceProvider> _services;
            private readonly IServiceCollection _serviceCollection;

            public Context()
            {
                _app = new();
                _serviceCollection = SetupServices();
                _services = new Lazy<IServiceProvider>(_serviceCollection.BuildServiceProvider);
            }

            private IServiceCollection SetupServices()
            {
                var services = Program.CreateServiceCollection(_app);
                var discoveryService = Substitute.For<IProjectDiscoveryService>();
                var projectAnalysisService = Substitute.For<IProjectAnalysisService>();
                var restoreService = Substitute.For<IDotNetRestoreService>();
                var project = new Project("Something", "/some/path.csproj", Enumerable.Empty<Uri>(), new NuGetVersion("1.0"));
                var targetFramework = new TargetFramework(new NuGetFramework("dummy"));
                var dependency = new Dependency("fake", new VersionRange(new NuGetVersion("1.0")), new NuGetVersion("1.0"), false, false, false, false);
                targetFramework.Dependencies.Add(dependency);
                project.TargetFrameworks.Add(targetFramework);
                restoreService.Restore(Arg.Any<string>()).Returns(new RunStatus(string.Empty, string.Empty, 0));
                projectAnalysisService.AnalyzeProject(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>()).Returns(new List<Project>() { project });
                discoveryService.DiscoverProjects(Arg.Any<string>(), Arg.Any<bool>()).Returns(
                   new List<string>
                   {
                        "/a/file.csproj"
                   });
                return services
                   .AddSingleton<IConsole>(new MockConsole())
                   .AddSingleton(discoveryService)
                   .AddSingleton(projectAnalysisService)
                   .AddSingleton(restoreService);
            }

            public IServiceProvider Services => _services.Value;

            public IDotNetAddPackageService PackageServiceImplementation
            {
                get
                {
                    var service = PackageService is ConfiguredAddPackageService s
                        ? s
                        : throw new InvalidOperationException("Unexpected service type");
                    var field = service.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Single(f => f.Name.Equals("_configuredService", StringComparison.Ordinal));
                    var value = field.GetValue(service) is Lazy<IDotNetAddPackageService> l
                        ? l
                        : throw new InvalidOperationException("Unexpected field type");
                    return value.Value;
                }
            }
            public IDotNetAddPackageService PackageService => Services.GetRequiredService<IDotNetAddPackageService>();

            public void StartApp(params string[] args)
            {
                _app.Conventions
                    .UseDefaultConventions()
                    .UseConstructorInjection(Services);
                _app.Execute(args);
            }

            public void Dispose()
            {
                _app.Dispose();
            }

            public Context WithModification(Action<IServiceCollection> action)
            {
                action(_serviceCollection);
                return this;
            }
        }
    }
}
