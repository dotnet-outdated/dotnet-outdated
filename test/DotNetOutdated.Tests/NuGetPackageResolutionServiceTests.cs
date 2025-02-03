using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests;

public class NuGetPackageResolutionServiceTests
{
    private readonly string _packageName1 = "MyPackage";
    private readonly string _packageName2 = "YourPackage";
    private readonly string _packageName3 = "OtherPackage";
    private readonly NuGetPackageResolutionService _nuGetPackageResolutionService;

    public NuGetPackageResolutionServiceTests()
    {
        List<NuGetVersion> availableVersions1 =
        [
            new("1.1.0"),
            new("1.2.0"),
            new("1.2.2"),
            new("1.3.0-pre"),
            new("1.3.0"),
            new("1.4.0-pre"),
            new("2.0.0"),
            new("2.1.0"),
            new("2.2.0-pre.1"),
            new("2.2.0-pre.2"),
            new("3.0.0-pre.1"),
            new("3.0.0-pre.2"),
            new("3.1.0-pre.1"),
            new("4.0.0-pre.1")
        ];

        List<NuGetVersion> availableVersions2 =
        [
            new("3.0.0-preview.1.123"),
            new("3.0.0"),
            new("3.0.1"),
            new("3.1.0-preview.1.123"),
            new("3.1.0"),
            new("3.1.1-preview.1.123"),
            new("3.1.1"),
            new("6.0.0-preview.1.123"),
            new("6.0.0"),
            new("6.0.1"),
            new("7.0.0"),
            new("7.0.1"),
            new("8.0.0-preview.1.123"),
            new("8.0.0-preview.1.124"),
            new("8.0.0-preview.2.125"),
            new("8.0.0-preview.3.126"),
            new("8.0.0-rc.1.127"),
            new("8.0.0-rc.2.128")
        ];

        List<NuGetVersion> availableVersions3 = [];

        var nuGetPackageInfoService = Substitute.For<INuGetPackageInfoService>();
        nuGetPackageInfoService.GetAllVersions(_packageName1, Arg.Any<List<Uri>>(), Arg.Any<bool>(), Arg.Any<NuGetFramework>(), Arg.Any<string>(), Arg.Any<bool>(), 0, Arg.Any<bool>())
            .Returns(availableVersions1);
        nuGetPackageInfoService.GetAllVersions(_packageName2, Arg.Any<List<Uri>>(), Arg.Any<bool>(), Arg.Any<NuGetFramework>(), Arg.Any<string>(), Arg.Any<bool>(), 0, Arg.Any<bool>())
            .Returns(availableVersions2);
        nuGetPackageInfoService.GetAllVersions(_packageName3, Arg.Any<List<Uri>>(), Arg.Any<bool>(), Arg.Any<NuGetFramework>(), Arg.Any<string>(), Arg.Any<bool>(), 0, Arg.Any<bool>())
            .Returns(availableVersions3);

        _nuGetPackageResolutionService = new NuGetPackageResolutionService(nuGetPackageInfoService);
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
    public async Task ResolvesVersionCorrectly(string current, VersionLock versionLock, PrereleaseReporting prerelease, string latest)
    {
        // Act
        var latestVersion = await _nuGetPackageResolutionService.ResolvePackageVersions(_packageName1, NuGetVersion.Parse(current), new List<Uri>(), VersionRange.Parse(current), versionLock, prerelease, null, null, null, false, 0);

        // Assert
        Assert.Equal(NuGetVersion.Parse(latest), latestVersion);
    }

    [Theory]
    [InlineData("3.0.0", VersionLock.None, PrereleaseReporting.Auto, null, "7.0.1")]
    [InlineData("3.0.0", VersionLock.None, PrereleaseReporting.Always, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Never, null, "8.0.0-preview.1.123")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Never, "preview.1", "8.0.0-preview.1.123")]
    [InlineData("3.0.0", VersionLock.Major, PrereleaseReporting.Auto, null, "3.1.1")]
    [InlineData("3.0.0", VersionLock.Minor, PrereleaseReporting.Auto, null, "3.0.1")]
    [InlineData("3.1.0", VersionLock.Minor, PrereleaseReporting.Auto, null, "3.1.1")]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, null, "8.0.0-rc.2.128")]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, "rc.2", "8.0.0-rc.2.128")]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, null, "8.0.0-rc.2.128")]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, "rc.2", "8.0.0-rc.2.128")]
    [InlineData("3.1.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Auto, null, "3.1.1")]
    [InlineData("3.1.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Always, null, "3.1.1")]
    [InlineData("3.1.0-preview.1.123", VersionLock.Major, PrereleaseReporting.Auto, null, "3.1.1")]
    [InlineData("3.1.0-preview.1.123", VersionLock.Major, PrereleaseReporting.Always, null, "3.1.1")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, "rc.2", "8.0.0-rc.2.128")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, "rc.2", "8.0.0-rc.2.128")]
    [InlineData("8.0.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Auto, null, "8.0.0-preview.3.126")]
    [InlineData("8.0.0-preview.3.126", VersionLock.Minor, PrereleaseReporting.Always, null, "8.0.0-preview.3.126")]
    [InlineData("8.0.0-preview.3.126", VersionLock.Minor, PrereleaseReporting.Always, "preview.3", "8.0.0-preview.3.126")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Auto, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Auto, "rc.1", "8.0.0-rc.1.127")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Always, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Always, "rc.1", "8.0.0-rc.1.127")]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, "rc", "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, "rc.2", "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, null, "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, "rc", "8.0.0-rc.2.128")]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, "rc.2", "8.0.0-rc.2.128")]
    public async Task ResolvesVersionCorrectlyWithParts(string current, VersionLock versionLock, PrereleaseReporting prerelease, string prereleaseLabel, string latest)
    {
        // Act
        var latestVersion = await _nuGetPackageResolutionService.ResolvePackageVersions(_packageName2, NuGetVersion.Parse(current), [], VersionRange.Parse(current), versionLock, prerelease, prereleaseLabel, null, null, false, 0);

        // Assert
        Assert.Equal(NuGetVersion.Parse(latest), latestVersion);
    }

    [Theory]
    [InlineData("3.0.0", VersionLock.None, PrereleaseReporting.Auto, null)]
    [InlineData("3.0.0", VersionLock.None, PrereleaseReporting.Always, null)]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Never, null)]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Never, "preview.1")]
    [InlineData("3.0.0", VersionLock.Major, PrereleaseReporting.Auto, null)]
    [InlineData("3.0.0", VersionLock.Minor, PrereleaseReporting.Auto, null)]
    [InlineData("3.1.0", VersionLock.Minor, PrereleaseReporting.Auto, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, "rc.2")]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, "rc.2")]
    [InlineData("3.1.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Auto, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Always, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.Major, PrereleaseReporting.Auto, null)]
    [InlineData("3.1.0-preview.1.123", VersionLock.Major, PrereleaseReporting.Always, null)]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, null)]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Always, "rc.2")]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, null)]
    [InlineData("8.0.0-preview.1.123", VersionLock.None, PrereleaseReporting.Auto, "rc.2")]
    [InlineData("8.0.0-preview.1.123", VersionLock.Minor, PrereleaseReporting.Auto, null)]
    [InlineData("8.0.0-preview.3.126", VersionLock.Minor, PrereleaseReporting.Always, null)]
    [InlineData("8.0.0-preview.3.126", VersionLock.Minor, PrereleaseReporting.Always, "preview.3")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Auto, null)]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Auto, "rc.1")]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Always, null)]
    [InlineData("8.0.0-rc.1.127", VersionLock.Major, PrereleaseReporting.Always, "rc.1")]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, null)]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, "rc")]
    [InlineData("8.0.0-rc.2.128", VersionLock.Major, PrereleaseReporting.Never, "rc.2")]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, null)]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, "rc")]
    [InlineData("8.0.0-rc.2.128", VersionLock.None, PrereleaseReporting.Never, "rc.2")]
    public async Task ResolvesVersionCorrectlyIfPackageIsLatest(string current, VersionLock versionLock, PrereleaseReporting prerelease, string prereleaseLabel)
    {
        // Act
        var latestVersion = await _nuGetPackageResolutionService.ResolvePackageVersions(_packageName3, NuGetVersion.Parse(current), [], VersionRange.Parse(current), versionLock, prerelease, prereleaseLabel, null, null, false, 0);

        // Assert
        Assert.Equal(NuGetVersion.Parse(current), latestVersion);
    }
}