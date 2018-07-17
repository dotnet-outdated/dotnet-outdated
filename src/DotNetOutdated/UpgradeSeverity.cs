using System;
using NuGet.Versioning;

namespace DotNetOutdated
{
    public enum UpgradeSeverity
    {
        None,
        Prerelease,
        Major,
        Minor,
        Patch
    }

    public static class UpgradeSeverityExtensions
    {
        private static UpgradeSeverity GetUpgradeSeverity(NuGetVersion latestVersion, NuGetVersion resolvedVersion)
        {
            if (latestVersion == null || resolvedVersion == null)
                return UpgradeSeverity.None;

            if (resolvedVersion.IsPrerelease)
                return UpgradeSeverity.Prerelease;
            if (latestVersion.Major > resolvedVersion.Major)
                return UpgradeSeverity.Major;
            if (latestVersion.Minor > resolvedVersion.Minor)
                return UpgradeSeverity.Minor;
            if (latestVersion.Patch > resolvedVersion.Patch)
                return UpgradeSeverity.Patch;

            return UpgradeSeverity.None;
        }

        public static UpgradeSeverity DiffWhenUpgradingFrom(this NuGetVersion latestVersion, NuGetVersion resolvedVersion)
            => GetUpgradeSeverity(latestVersion, resolvedVersion);
        public static UpgradeSeverity DiffWhenUpgradingTo(this NuGetVersion resolvedVersion, NuGetVersion latestVersion)
            => GetUpgradeSeverity(latestVersion, resolvedVersion);

        public static ConsoleColor GetLatestVersionColor(this UpgradeSeverity diff)
        {
            switch (diff)
            {
                case UpgradeSeverity.Prerelease:
                case UpgradeSeverity.Major: return Constants.ReporingColors.MajorVersionUpgrade;
                case UpgradeSeverity.Minor: return Constants.ReporingColors.MinorVersionUpgrade;
                case UpgradeSeverity.Patch: return Constants.ReporingColors.PatchVersionUpgrade;
                case UpgradeSeverity.None:
                default: return Console.ForegroundColor;
            }
        }
    }
}
