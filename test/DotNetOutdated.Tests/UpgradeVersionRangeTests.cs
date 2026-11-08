using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class UpgradeVersionRangeTests
    {
        [Theory]
        // Upper bound is kept while the new version is inside the declared range
        [InlineData("[6.0.13,7)", "6.0.36", "[6.0.36, 7.0.0)")]
        [InlineData("[12.0.1,14.0.0)", "13.0.4", "[13.0.4, 14.0.0)")]
        [InlineData("(6.0.0,8)", "7.0.0", "[7.0.0, 8.0.0)")]
        [InlineData("(,8.0.0)", "7.0.0", "[7.0.0, 8.0.0)")]
        [InlineData("[6.0.13,7]", "7.0.0", "[7.0.0]")]
        [InlineData("[6.*, 8)", "7.0.20", "[7.0.20, 8.0.0)")]
        // No upper bound: plain version, as before
        [InlineData("6.0.13", "8.0.24", "8.0.24")]
        [InlineData("[6.0.13,)", "8.0.24", "8.0.24")]
        [InlineData("6.*", "8.0.24", "8.0.24")]
        // New version outside the declared range (e.g. --maximum-version): drop the bound
        [InlineData("[6.0.13,7)", "8.0.24", "8.0.24")]
        [InlineData("[6.0.13,7]", "7.0.1", "7.0.1")]
        public void Compute_ReturnsExpectedRange(string original, string latest, string expected)
        {
            var result = UpgradeVersionRange.Compute(VersionRange.Parse(original), NuGetVersion.Parse(latest), isTransitive: false);

            Assert.Equal(expected, result.ToShortString());
        }

        [Fact]
        public void Compute_WithoutOriginalRange_ReturnsPlainVersion()
        {
            var result = UpgradeVersionRange.Compute(null, NuGetVersion.Parse("1.2.3"), isTransitive: false);

            Assert.Equal(new VersionRange(NuGetVersion.Parse("1.2.3")), result);
        }

        [Fact]
        public void Compute_ForTransitivePackage_DoesNotInheritParentConstraint()
        {
            // e.g. AWSSDK.S3 depends on AWSSDK.Core [3.7.0, 3.8.0); promoting AWSSDK.Core must not copy that bound
            var result = UpgradeVersionRange.Compute(VersionRange.Parse("[3.7.0, 3.8.0)"), NuGetVersion.Parse("3.7.501.1"), isTransitive: true);

            Assert.Equal("3.7.501.1", result.ToShortString());
        }
    }
}
