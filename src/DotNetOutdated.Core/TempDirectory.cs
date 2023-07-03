using System;
using System.IO;

namespace DotNetOutdated
{
    internal class TempDirectory : IDisposable
    {
        private readonly string _tempPath;
        private readonly string _tempDirName;

        public TempDirectory()
        {
            _tempPath = Path.GetTempPath();
            _tempDirName = Path.GetRandomFileName();
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, true);
        }

        public string DirectoryPath => Path.Combine(_tempPath, _tempDirName);
    }
}
