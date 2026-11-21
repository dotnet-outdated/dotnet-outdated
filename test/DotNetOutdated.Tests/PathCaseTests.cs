using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class PathCaseTests
    {
        private static readonly string StoredPath = XFS.Path(@"c:\repo\lib\Shared.cs");

        [Fact]
        public void OnDisk_SpellsAPathAsTheFileSystemStoresIt()
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [StoredPath] = new(string.Empty) });
            var written = XFS.Path(@"c:\repo\LIB\shared.cs");

            // Where case is ignored, the path written names the stored file; where it matters, it names nothing.
            var caseSensitive = !fileSystem.File.Exists(written);

            Assert.Equal(StoredPath, PathCase.OnDisk(fileSystem, StoredPath));
            Assert.Equal(caseSensitive ? written : StoredPath, PathCase.OnDisk(fileSystem, written));
        }

        [Fact]
        public void OnDisk_KeepsTwoFilesWhosePathsDifferOnlyInCaseApart()
        {
            var otherPath = XFS.Path(@"c:\repo\lib\shared.cs");
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [StoredPath] = new("a"),
                [otherPath] = new("b"),
            });
            var caseSensitive = fileSystem.File.ReadAllText(StoredPath) != fileSystem.File.ReadAllText(otherPath);

            Assert.Equal(caseSensitive, PathCase.OnDisk(fileSystem, StoredPath) != PathCase.OnDisk(fileSystem, otherPath));
        }

        [Fact]
        public void OnDisk_KeepsThePartsThatDoNotExistAsWritten()
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [StoredPath] = new(string.Empty) });
            var missing = XFS.Path(@"c:\repo\lib\Missing\File.cs");

            Assert.Equal(missing, PathCase.OnDisk(fileSystem, missing));
        }
    }
}
