using NuGet.Versioning;

namespace DotNetOutdated.Core
{
    internal static class VersionRangeExtensions
    {
        /// <summary>
        /// Formats a range for writing into a project file. A plain minimum version (<c>[1.2.3, )</c>) is
        /// written exactly as <see cref="NuGetVersion.ToString()"/> would, so existing single-version
        /// upgrades are unchanged; anything else uses NuGet's short range syntax, e.g. <c>[1.2.3, 2.0.0)</c>.
        /// </summary>
        public static string ToVersionString(this VersionRange versionRange) =>
            versionRange is { HasLowerBound: true, IsMinInclusive: true, HasUpperBound: false, IsFloating: false }
                ? versionRange.MinVersion.ToString()
                : versionRange.ToShortString();
    }
}
