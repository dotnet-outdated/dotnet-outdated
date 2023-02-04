using System;
using System.IO.Abstractions.TestingHelpers;

namespace DotNetOutdated.Tests
{
    internal static class Singletons
    {
        public static MockFileData NullObject { get; } = new(string.Empty)
        {
            LastWriteTime = new DateTime(1601, 01, 01, 00, 00, 00, DateTimeKind.Utc),
            LastAccessTime = new DateTime(1601, 01, 01, 00, 00, 00, DateTimeKind.Utc),
            CreationTime = new DateTime(1601, 01, 01, 00, 00, 00, DateTimeKind.Utc),
        };
    }
}
