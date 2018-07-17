using System;

namespace DotNetOutdated
{
    public static class Constants
    {
        public static class ReporingColors
        {
            public const ConsoleColor MajorVersionUpgrade = ConsoleColor.Red;
            public const ConsoleColor MinorVersionUpgrade = ConsoleColor.Yellow;
            public const ConsoleColor PatchVersionUpgrade = ConsoleColor.Green;
        }
    }
}