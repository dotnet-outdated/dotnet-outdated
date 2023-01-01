using NuGet.Versioning;
using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services
{
    public class CentralPackageVersionManagementService : ICentralPackageVersionManagementService
    {
        private readonly IFileSystem _fileSystem;
        private readonly IDotNetRestoreService _dotNetRestoreService;

        public CentralPackageVersionManagementService(IFileSystem fileSystem, IDotNetRestoreService dotNetRestoreService)
        {
            _fileSystem = fileSystem;
            _dotNetRestoreService = dotNetRestoreService;
        }

        public RunStatus AddPackage(string projectFilePath, string packageName, NuGetVersion version, bool noRestore)
        {
            RunStatus status = new RunStatus(string.Empty, string.Empty, 0);

            try
            {
                IFileInfo projectFile = _fileSystem.FileInfo.New(projectFilePath);
                bool foundCPVMFile = false;
                IDirectoryInfo directoryInfo = projectFile.Directory;

                while (!foundCPVMFile && directoryInfo != null)
                {
                    IFileInfo[] files = directoryInfo.GetFiles("*", SearchOption.TopDirectoryOnly);
                    IFileInfo cpvmFile = files.SingleOrDefault(f => f.Name.Equals("Directory.Packages.Props", StringComparison.OrdinalIgnoreCase));

                    if (cpvmFile != null)
                    {
                        string fileContent = string.Empty;

                        using (StreamReader reader = cpvmFile.OpenText())
                        {
                            fileContent = reader.ReadToEnd();
                        }

                        if (fileContent.IndexOf($"\"{packageName}\"", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            string newFileContent = Regex.Replace(fileContent, $"(<PackageVersion\\s*(?:Include|Update)=\"{packageName}\"\\s*Version=\")([^\"]*)(\".*\\/>)", m => $"{m.Groups[1].Captures[0].Value}{version}{m.Groups[3].Captures[0].Value}");

                            if (newFileContent != fileContent)
                            {
                                _fileSystem.File.WriteAllText(cpvmFile.FullName, newFileContent);
                            }

                            foundCPVMFile = true;
                        }
                    }

                    if (!foundCPVMFile)
                    {
                        directoryInfo = directoryInfo.Parent;
                    }
                }

                if (!noRestore)
                {
                    RunStatus restoreStatus = _dotNetRestoreService.Restore(projectFilePath);

                    if (!restoreStatus.IsSuccess)
                    {
                        status = new RunStatus(string.Empty, "Failed to restore project after upgrading!", -1);
                    }
                }
            }
            catch (Exception)
            {
                status = new RunStatus(string.Empty, "Failed to update the central package version management file!", -1);
            }

            return status;
        }
    }
}
