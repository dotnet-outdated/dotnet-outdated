using System;
using System.IO;

namespace DotNetOutdated
{
    class TempDirectory : IDisposable
    {
        private string tempPath;
        private string tempDirName;

        public TempDirectory()
        {
            tempPath = Path.GetTempPath();
            tempDirName = Path.GetRandomFileName();
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, true);
        }

        public string DirectoryPath
        {
            get => Path.Combine(tempPath, tempDirName);
        }
    }
}
