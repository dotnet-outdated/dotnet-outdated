using NuGet.Versioning;

namespace DotNetOutdated
{
    internal static class UpgradeVersionRange
    {
        /// <summary>
        /// Computes the version range to write when upgrading a reference to <paramref name="latestVersion"/>.
        /// The declared upper bound is kept when the new version still satisfies the declared range, e.g.
        /// <c>[6.0.13, 7.0.0)</c> upgraded to 6.0.36 becomes <c>[6.0.36, 7.0.0)</c>. Otherwise the result is a
        /// plain minimum version, so an upgrade past the range (e.g. via <c>--maximum-version</c>) never
        /// produces an unsatisfiable range. Floating versions are not preserved.
        /// </summary>
        public static VersionRange Compute(VersionRange originalVersionRange, NuGetVersion latestVersion)
        {
            if (originalVersionRange is { HasUpperBound: true } && originalVersionRange.Satisfies(latestVersion))
            {
                return new VersionRange(
                    minVersion: latestVersion,
                    includeMinVersion: true,
                    maxVersion: originalVersionRange.MaxVersion,
                    includeMaxVersion: originalVersionRange.IsMaxInclusive);
            }

            return new VersionRange(latestVersion);
        }
    }
}
