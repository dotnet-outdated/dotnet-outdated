using DotNetOutdated.Core.Models;
using DotNetOutdated.Models;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class DependencyUpgradeSeverityTests
    {
        public DependencyUpgradeSeverityTests()
        {
        }

        [Theory]
        [InlineData("1.2.3    ", "2.0.0    ")]
        [InlineData("1.0.13   ", "2.0.1    ")]
        [InlineData("12.15.16 ", "13.0.0   ")]
        public void DependencyUpgradeSeverityForMajorUpgrades(string resolved, string latest)
        {
            var resolvedVersion = new NuGetVersion(resolved);
            var latestVersion = new NuGetVersion(latest);

            var dependency = DependencyUpgradeSeverityTests.CreateAnalyzedDependency(resolvedVersion, latestVersion);

            Assert.Equal(DependencyUpgradeSeverity.Major, dependency.UpgradeSeverity);
        }

        [Theory]
        [InlineData("1.2.3    ", "1.3.0    ")]
        [InlineData("1.0.13   ", "1.4.13   ")]
        [InlineData("12.0.16  ", "12.18.0  ")]
        public void DependencyUpgradeSeverityForMinorUpgrades(string resolved, string latest)
        {
            var resolvedVersion = new NuGetVersion(resolved);
            var latestVersion = new NuGetVersion(latest);

            var dependency = DependencyUpgradeSeverityTests.CreateAnalyzedDependency(resolvedVersion, latestVersion);

            Assert.Equal(DependencyUpgradeSeverity.Minor, dependency.UpgradeSeverity);
        }

        [Theory]
        [InlineData("1.2.3    ", "1.2.3    ")]
        [InlineData("1.0.13   ", "1.0.13   ")]
        [InlineData("12.0.16  ", "12.0.16")]
        public void DependencyUpgradeSeverityForNoUpgrades(string resolved, string latest)
        {
            var resolvedVersion = new NuGetVersion(resolved);
            var latestVersion = new NuGetVersion(latest);

            var dependency = DependencyUpgradeSeverityTests.CreateAnalyzedDependency(resolvedVersion, latestVersion);

            Assert.Equal(DependencyUpgradeSeverity.None, dependency.UpgradeSeverity);
        }

        [Theory]
        [InlineData("1.2.3    ", "1.2.4    ")]
        [InlineData("1.0.13   ", "1.0.20   ")]
        [InlineData("12.0.16  ", "12.0.1542")]
        public void DependencyUpgradeSeverityForPatchUpgrades(string resolved, string latest)
        {
            var resolvedVersion = new NuGetVersion(resolved);
            var latestVersion = new NuGetVersion(latest);

            var dependency = DependencyUpgradeSeverityTests.CreateAnalyzedDependency(resolvedVersion, latestVersion);

            Assert.Equal(DependencyUpgradeSeverity.Patch, dependency.UpgradeSeverity);
        }

        [Theory]
        [InlineData("1.0.1-al ", "1.0.1-be ")]
        [InlineData("1.0.2-al ", "1.0.2    ")]
        public void DependencyUpgradeSeverityForPrereleaseUpgrades(string resolved, string latest)
        {
            var resolvedVersion = new NuGetVersion(resolved);
            var latestVersion = new NuGetVersion(latest);

            var dependency = DependencyUpgradeSeverityTests.CreateAnalyzedDependency(resolvedVersion, latestVersion);

            Assert.Equal(DependencyUpgradeSeverity.Major, dependency.UpgradeSeverity);
        }

        private static AnalyzedDependency CreateAnalyzedDependency(NuGetVersion resolvedVersion, NuGetVersion latestVersion)
        {
            return new AnalyzedDependency(new Dependency("Does not matter", VersionRange.All, resolvedVersion, false, false, false, false), latestVersion);
        }
    }
}
