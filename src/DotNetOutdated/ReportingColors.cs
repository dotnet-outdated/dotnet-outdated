using System;

namespace DotNetOutdated
{
    public sealed class ReportingColors
    {
        public readonly ConsoleColor ProjectName = ConsoleColor.Blue;
        public readonly ConsoleColor TargetFrameworkName = ConsoleColor.Cyan;
        public readonly ConsoleColor PackageName = ConsoleColor.Magenta;

        public readonly ConsoleColor MajorVersionUpgrade = ConsoleColor.Red;
        public readonly ConsoleColor MinorVersionUpgrade = ConsoleColor.Yellow;
        public readonly ConsoleColor PatchVersionUpgrade = ConsoleColor.Green;

        public readonly ConsoleColor UpgradeSuccess = ConsoleColor.Green;
        public readonly ConsoleColor UpgradeFailure = ConsoleColor.Red;
    }
}
