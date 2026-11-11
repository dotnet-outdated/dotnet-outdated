#nullable enable

using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class ServiceRegistrationTests
    {
        [Fact]
        public void AddDotNetOutdated_RegistersEveryPublicService()
        {
            using var provider = new ServiceCollection()
                .AddDotNetOutdated()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            Assert.NotNull(provider.GetRequiredService<IProjectDiscoveryService>());
            Assert.NotNull(provider.GetRequiredService<IProjectAnalysisService>());
            Assert.NotNull(provider.GetRequiredService<IOutdatedDependencyService>());
            Assert.NotNull(provider.GetRequiredService<IDotNetPackageService>());
            Assert.NotNull(provider.GetRequiredService<INuGetPackageResolutionService>());
            Assert.NotNull(provider.GetRequiredService<INuGetPackageInfoService>());
            Assert.NotNull(provider.GetRequiredService<DotNetRunnerOptions>());
        }

        [Fact]
        public void AddDotNetOutdated_KeepsServicesThatAreAlreadyRegistered()
        {
            var feed = Substitute.For<INuGetPackageInfoService>();

            using var provider = new ServiceCollection()
                .AddSingleton(feed)
                .AddDotNetOutdated()
                .BuildServiceProvider();

            Assert.Same(feed, provider.GetRequiredService<INuGetPackageInfoService>());
        }

        [Fact]
        public void AddDotNetOutdated_SendsUpgradeWarningsToTheRegisteredReporter()
        {
            // A version expression that can't be kept is replaced by a literal, with a warning.
            var projectPath = XFS.Path(@"c:\repo\App.csproj");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [projectPath] = new("""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <V>1.0.0</V>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Foo" Version="$(V)-preview" />
                      </ItemGroup>
                    </Project>
                    """),
            });
            var warnings = Substitute.For<IWarningReporter>();
            var runner = Substitute.For<IDotNetRunner>();
            runner.Run(default, default!).ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, 0));

            using var provider = new ServiceCollection()
                .AddSingleton<System.IO.Abstractions.IFileSystem>(fileSystem)
                .AddSingleton(runner)
                .AddSingleton(warnings)
                .AddDotNetOutdated()
                .BuildServiceProvider();

            provider.GetRequiredService<IDotNetPackageService>().Upgrade(projectPath, "Foo", "net9.0", new VersionRange(new NuGetVersion("2.0.0")));

            warnings.Received().Warn(Arg.Is<string>(message => message.Contains("Foo")));
        }
    }
}
