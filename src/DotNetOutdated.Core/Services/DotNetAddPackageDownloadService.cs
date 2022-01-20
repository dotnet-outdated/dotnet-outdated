using System.IO;
using System.IO.Abstractions;
using System.Xml;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services
{
    public class DotNetAddPackageDownloadService : IDotNetAddPackageService
    {
        private readonly IDotNetRunner _dotNetRunner;
        private readonly IFileSystem _fileSystem;

        public DotNetAddPackageDownloadService(IDotNetRunner dotNetRunner, IFileSystem fileSystem)
        {
            _dotNetRunner = dotNetRunner;
            _fileSystem = fileSystem;
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSource = false)
        {
            using (var stream = _fileSystem.FileStream.Create(projectPath, FileMode.Open, FileAccess.ReadWrite))
            {
                // Read xml from stream
                var document = new XmlDocument();
                document.Load(stream);

                // Get PackageDownload element with matching package name
                var packageDownloadElement = document.SelectSingleNode($"/Project/ItemGroup/PackageDownload[@Include='{packageName}']");

                // Replace version of element with version from parameter
                packageDownloadElement.Attributes["Version"].Value = $"[{version}]";

                // Write xml back to stream
                stream.Position = 0;
                stream.SetLength(0);
                document.Save(stream);
            }

            return _dotNetRunner.Run(_fileSystem.Path.GetDirectoryName(projectPath), new[] { "restore" });
        }
    }
}