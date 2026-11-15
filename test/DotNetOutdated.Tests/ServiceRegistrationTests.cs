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
            Assert.NotNull(provider.GetRequiredService<IReferenceUpgrader>());
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
    }
}
