using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNetOutdated.Services;
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
                new NuGetVersion("2.2.0-pre")
            };
            
            var nuGetPackageInfoService = new Mock<INuGetPackageInfoService>();
            nuGetPackageInfoService.Setup(service => service.GetAllVersions(packageName, It.IsAny<List<Uri>>(), It.IsAny<bool>(), It.IsAny<NuGetFramework>(), It.IsAny<string>()))
                .ReturnsAsync(availableVersions);
            
            _nuGetPackageResolutionService = new NuGetPackageResolutionService(nuGetPackageInfoService.Object);
            }

        [Theory]
        [InlineData("1.2.0", VersionLock.None, PrereleaseReporting.Auto, "2.1.0")]
        [InlineData("1.2.0", VersionLock.None, PrereleaseReporting.Always, "2.2.0-pre")]
        [InlineData("1.3.0-pre", VersionLock.None, PrereleaseReporting.Auto, "2.2.0-pre")]
        [InlineData("1.3.0-pre", VersionLock.None, PrereleaseReporting.Never, "2.1.0")]
        [InlineData("1.2.0", VersionLock.Major, PrereleaseReporting.Auto, "1.3.0")]
        [InlineData("1.2.0", VersionLock.Minor, PrereleaseReporting.Auto, "1.2.2")]
        public async Task ResolvesVersion_Correctly(string current, VersionLock versionLock, PrereleaseReporting prerelease, string latest)
        {
            // Arrange
            
            // Act
            var latestVersion = await _nuGetPackageResolutionService.ResolvePackageVersions(packageName, NuGetVersion.Parse(current), new List<Uri>(), VersionRange.Parse(current), versionLock, prerelease, null, null);
            
            // Assert
            Assert.Equal(NuGetVersion.Parse(latest), latestVersion);
        }
    }
}