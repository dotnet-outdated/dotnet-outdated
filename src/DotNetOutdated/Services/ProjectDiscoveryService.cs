using System;
using System.IO;
using System.IO.Abstractions;
using System.Resources;
using DotNetOutdated.Exceptions;

namespace DotNetOutdated.Services
{
    internal class ProjectDiscoveryService : IProjectDiscoveryService
    {
        private readonly IFileSystem _fileSystem;

        public ProjectDiscoveryService(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }
        
        public string DiscoverProject(string path)
        {
            if (!(_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path)))
                throw new CommandValidationException(string.Format(Resources.ValidationErrorMessages.DirectoryOrFileDoesNotExist, path));

            var fileAttributes = _fileSystem.File.GetAttributes(path);
            
            // If a directory was passed in, search for a .sln or .csproj file
            if (fileAttributes.HasFlag(FileAttributes.Directory))
            {
                // Search for solution(s)
                var solutionFiles = _fileSystem.Directory.GetFiles(path, "*.sln");
                if (solutionFiles.Length == 1)
                    return _fileSystem.Path.GetFullPath(solutionFiles[0]);
                
                if (solutionFiles.Length > 1)
                    throw new CommandValidationException(string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleSolutions, path));
                
                // We did not find any solutions, so try and find individual projects
                var projectFiles = _fileSystem.Directory.GetFiles(path, "*.csproj");
                if (projectFiles.Length == 1)
                    return _fileSystem.Path.GetFullPath(projectFiles[0]);
                
                if (projectFiles.Length > 1)
                    throw new CommandValidationException(string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleProjects, path));

                // At this point the path contains no solutions or projects, so throw an exception
                throw new CommandValidationException(string.Format(Resources.ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, path));
            }

            // If a .sln or .csproj file was passed, just return that
            if ((string.Compare(_fileSystem.Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase) == 0) ||
                (string.Compare(_fileSystem.Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase) == 0))
                return _fileSystem.Path.GetFullPath(path);

            // At this point, we know the file passed in is not a valid project or solution
            throw new CommandValidationException(string.Format(Resources.ValidationErrorMessages.FileNotAValidSolutionOrProject, path));
        }
    }
}