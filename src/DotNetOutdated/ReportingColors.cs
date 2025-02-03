using System;

namespace DotNetOutdated;

public sealed class ReportingColors
{
    public ConsoleColor ProjectName => ConsoleColor.Blue;
    public ConsoleColor TargetFrameworkName => ConsoleColor.Cyan;
    public ConsoleColor PackageName => ConsoleColor.Magenta;

    public ConsoleColor MajorVersionUpgrade => ConsoleColor.Red;
    public ConsoleColor MinorVersionUpgrade => ConsoleColor.Yellow;
    public ConsoleColor PatchVersionUpgrade => ConsoleColor.Green;

    public ConsoleColor UpgradeSuccess => ConsoleColor.Green;
    public ConsoleColor UpgradeFailure => ConsoleColor.Red;
}