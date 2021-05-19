using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Services;
using Moq;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class NuGetPackageResolutionServiceTests
    {
        private string packageName = "MyPackage";
        private NuGetPackageResolutionService _nuGetPackageResolutionService;

        public NuGetPackageResolutionServiceTests()
        {
            List<NuGetVersion> availableVersions = new List<NuGetVersion>
            {
                new NuGetVersion("1.1.0"),
                new NuGetVersion("1.2.0"),
                new NuGetVersion("1.2.2"),
                new NuGetVersion("1.3.0-pre"),
                new NuGetVersion("1.3.0"),
                new NuGetVersion("1.4.0-pre"),
                new NuGetVersion("2.0.0"),
                new NuGetVersion("2.1.0"),
                new NuGetVersion("2.2.0-pre.1"),
                new NuGetVersion("2.2.0-pre.2"),
                new NuGetVersion("3.0.0-pre.1"),
                new NuGetVersion("3.0.0-pre.2"),
                new NuGetVersion("3.1.0-pre.1"),
                new NuGetVersion("4.0.0-pre.1")
            };
            
            var nuGetPackageInfoService = new Mock<INuGetPackageInfoService>();
            nuGetPackageInfoService.Setup(service => service.GetAllVersions(packageName, It.IsAny<List<Uri>>(), It.IsAny<bool>(), It.IsAny<NuGetFramework>(), It.IsAny<string>(), It.IsAny<bool>(), 0))
                .ReturnsAsync(availableVersions);
            
            _nuGetPackageResolutionService = new NuGetPackageResolutionService(nuGetPackageInfoService.Object);
        }

        [Theory]
        [InlineData("1.2.0", VersionLock.None, PrereleaseReporting.Auto, "2.1.0")]
        [InlineData("1.2.0", VersionLock.None, PrereleaseReporting.Always, "4.0.0-pre.1")]
        [InlineData("1.3.0-pre", VersionLock.None, PrereleaseReporting.Auto, "4.0.0-pre.1")]
        [InlineData("1.3.0-pre", VersionLock.None, PrereleaseReporting.Never, "2.1.0")]
        [InlineData("1.2.0", VersionLock.Major, PrereleaseReporting.Auto, "1.3.0")]
        [InlineData("1.2.0", VersionLock.Minor, PrereleaseReporting.Auto, "1.2.2")]
        [InlineData("3.0.0-pre.1", VersionLock.None, PrereleaseReporting.Never, "3.0.0-pre.1")]
        [InlineData("3.0.0-pre.1", VersionLock.None, PrereleaseReporting.Always, "4.0.0-pre.1")]
        [InlineData("3.0.0-pre.1", VersionLock.None, PrereleaseReporting.Auto, "4.0.0-pre.1")]
        [InlineData("3.0.0-pre.1", VersionLock.Minor, PrereleaseReporting.Auto, "3.0.0-pre.2")]
        [InlineData("3.0.0-pre.1", VersionLock.Minor, PrereleaseReporting.Always, "3.0.0-pre.2")]
        [InlineData("3.0.0-pre.1", VersionLock.Major, PrereleaseReporting.Auto, "3.1.0-pre.1")]
        [InlineData("3.0.0-pre.1", VersionLock.Major, PrereleaseReporting.Always, "3.1.0-pre.1")]
        public async Task ResolvesVersion_Correctly(string current, VersionLock versionLock, PrereleaseReporting prerelease, string latest)
        {
            // Arrange
            
            // Act
            var latestVersion = await _nuGetPackageResolutionService.ResolvePackageVersions(packageName, NuGetVersion.Parse(current), new List<Uri>(), VersionRange.Parse(current), versionLock, prerelease, null, null, false, 0);
            
            // Assert
            Assert.Equal(NuGetVersion.Parse(latest), latestVersion);
        }
    }
}