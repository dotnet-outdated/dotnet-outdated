using DotNetOutdated.Core;
using DotNetOutdated.Core.Exceptions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace DotNetOutdated.Core.Services
{
    public sealed class ProjectDiscoveryService : IProjectDiscoveryService
    {
        private readonly IFileSystem _fileSystem;

        public ProjectDiscoveryService(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public IList<string> DiscoverProjects(string path, bool recursive = false, bool includeFileBasedApps = false)
        {
            if (!(_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path)))
                throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryOrFileDoesNotExist, path));

            var fileAttributes = _fileSystem.File.GetAttributes(path);

            // If a directory was passed in, search for a .sln or .csproj file
            if (fileAttributes.HasFlag(FileAttributes.Directory))
            {
                // If we are in recursive mode, find all individual projects recursively
                if (recursive)
                {
                    string[] recursiveProjectFiles;
                    if (includeFileBasedApps)
                    {
                        var recursiveDiscoveryResult = DiscoverRecursiveProjectFiles(path, isInProjectCone: false);
                        recursiveProjectFiles = recursiveDiscoveryResult.ProjectFiles
                            .Concat(recursiveDiscoveryResult.FileBasedApps)
                            .ToArray();
                    }
                    else
                    {
                        recursiveProjectFiles = _fileSystem.Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories)
                            .Concat(_fileSystem.Directory.GetFiles(path, "*.fsproj", SearchOption.AllDirectories))
                            .ToArray();
                    }

                    if (recursiveProjectFiles.Length > 0)
                        return recursiveProjectFiles;

                    // At this point the path contains no solutions or projects, so throw an exception
                    throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, path));
                }

                // Search for solution(s)
                var solutionFiles = _fileSystem.Directory.GetFiles(path, "*.sln");
                if (solutionFiles.Length == 1)
                    return new[] { _fileSystem.Path.GetFullPath(solutionFiles[0]) };

                if (solutionFiles.Length > 1)
                    throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryContainsMultipleSolutions, path));

                solutionFiles = _fileSystem.Directory.GetFiles(path, "*.slnx");
                if (solutionFiles.Length == 1)
                    return new[] { _fileSystem.Path.GetFullPath(solutionFiles[0]) };

                if (solutionFiles.Length > 1)
                    throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryContainsMultipleSolutions, path));

                // We did not find any solutions, so try and find individual projects
                var projectFiles = _fileSystem.Directory.GetFiles(path, "*.csproj").Concat(_fileSystem.Directory.GetFiles(path, "*.fsproj")).ToArray();
                if (projectFiles.Length == 1)
                    return new[] { _fileSystem.Path.GetFullPath(projectFiles[0]) };

                if (projectFiles.Length > 1)
                    throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryContainsMultipleProjects, path));

                // At this point the path contains no solutions or projects, so throw an exception
                throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, path));
            }

            // If a solution file or project file was passed, just return that
            if (path.IsSolutionFile() || path.IsProjectFile() || path.IsCSharpFile())
            {
                return new[] { _fileSystem.Path.GetFullPath(path) };
            }

            // At this point, we know the file passed in is not a valid project or solution
            throw new CommandValidationException(string.Format(CultureInfo.InvariantCulture, Resources.ValidationErrorMessages.FileNotAValidSolutionOrProject, path));
        }

        private RecursiveDiscoveryResult DiscoverRecursiveProjectFiles(string path, bool isInProjectCone)
        {
            var result = new RecursiveDiscoveryResult();
            var files = _fileSystem.Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
            var directoryContainsCSharpProject = false;

            foreach (var file in files)
            {
                if (file.IsCSharpProjectFile())
                {
                    result.ProjectFiles.Add(file);
                    directoryContainsCSharpProject = true;
                    continue;
                }

                if (file.IsProjectFile())
                {
                    result.ProjectFiles.Add(file);
                }
            }

            var currentDirectoryIsInProjectCone = isInProjectCone || directoryContainsCSharpProject;
            if (!currentDirectoryIsInProjectCone)
            {
                foreach (var file in files)
                {
                    if (file.IsCSharpFile() && StartsWithShebang(file))
                    {
                        result.FileBasedApps.Add(file);
                    }
                }
            }

            var childResults = _fileSystem.Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Select(directory => DiscoverRecursiveProjectFiles(directory, currentDirectoryIsInProjectCone))
                .ToArray();

            foreach (var childResult in childResults)
            {
                result.ProjectFiles.AddRange(childResult.ProjectFiles);
                result.FileBasedApps.AddRange(childResult.FileBasedApps);
            }

            return result;
        }

        private bool StartsWithShebang(string file)
        {
            using var stream = _fileSystem.File.OpenRead(file);
            Span<byte> bytes = stackalloc byte[5];
            var bytesRead = stream.Read(bytes);
            // Allow scripts saved with a UTF-8 BOM before the shebang.
            var offset = bytesRead >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

            return bytesRead - offset >= 2 && bytes[offset] == (byte)'#' && bytes[offset + 1] == (byte)'!';
        }

        private sealed class RecursiveDiscoveryResult
        {
            public List<string> ProjectFiles { get; } = new();

            public List<string> FileBasedApps { get; } = new();
        }
    }
}
