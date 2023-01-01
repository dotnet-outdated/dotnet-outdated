using System;

namespace DotNetOutdated
{
    public sealed class ReportingColors
    {
        public ConsoleColor ProjectName { get; } = ConsoleColor.Blue;
        public ConsoleColor TargetFrameworkName { get; } = ConsoleColor.Cyan;
        public ConsoleColor PackageName { get; } = ConsoleColor.Magenta;

        public ConsoleColor MajorVersionUpgrade { get; } = ConsoleColor.Red;
        public ConsoleColor MinorVersionUpgrade { get; } = ConsoleColor.Yellow;
        public ConsoleColor PatchVersionUpgrade { get; } = ConsoleColor.Green;

        public ConsoleColor UpgradeSuccess { get; } = ConsoleColor.Green;
        public ConsoleColor UpgradeFailure { get; } = ConsoleColor.Red;
    }
}
