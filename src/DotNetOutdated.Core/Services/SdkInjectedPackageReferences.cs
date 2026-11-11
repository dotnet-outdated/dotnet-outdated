using DotNetOutdated.Core.Models;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Finds package references that an MSBuild SDK or package adds to a project from its own props or targets files,
    /// such as <c>Cake.Generator</c> from <c>Cake.Sdk</c> or the MSTest packages from <c>MSTest.Sdk</c>. Their versions
    /// come from the SDK, so <c>dotnet add package</c> cannot upgrade them in the project.
    /// </summary>
    internal static class SdkInjectedPackageReferences
    {
        // Paths are case-sensitive on Linux, and case-insensitive by default on Windows and macOS.
        private static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        private static readonly StringComparer PathComparer = StringComparer.FromComparison(PathComparison);

        private static readonly string[] DirectoryBuildFileNames = ["Directory.Build.props", "Directory.Build.targets"];

        /// <summary>
        /// Determines whether a project can use an SDK other than the <c>Microsoft.NET.Sdk</c> family. It checks the
        /// project file and every <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> file in the project's
        /// directory and its parents, because a repository can import a custom SDK for all of its projects from there.
        /// </summary>
        /// <remarks>
        /// MSBuild imports only the nearest <c>Directory.Build.*</c> file, which may import the next one up. Checking
        /// every file above the project can only cause an unneeded evaluation, never a missed one. SDKs imported from
        /// other files, such as a custom <c>DirectoryBuildPropsPath</c>, are not detected.
        /// </remarks>
        public static bool UsesCustomSdk(IFileSystem fileSystem, string projectPath)
        {
            if (FileDeclaresCustomSdk(fileSystem, projectPath))
            {
                return true;
            }

            for (var directory = fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(projectPath));
                 !string.IsNullOrEmpty(directory);
                 directory = fileSystem.Path.GetDirectoryName(directory))
            {
                foreach (var fileName in DirectoryBuildFileNames)
                {
                    var path = fileSystem.Path.Combine(directory, fileName);
                    if (FileDeclaresCustomSdk(fileSystem, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether an MSBuild file declares an SDK other than the <c>Microsoft.NET.Sdk</c> family, in the
        /// <c>Sdk</c> attribute of the project, an <c>&lt;Sdk&gt;</c> element or an <c>&lt;Import Sdk&gt;</c>.
        /// Such SDKs are usually resolved from NuGet and can add package references of their own.
        /// </summary>
        /// <returns><c>false</c> when the file is missing, unreadable or not valid XML.</returns>
        private static bool FileDeclaresCustomSdk(IFileSystem fileSystem, string path) =>
            ProjectSdkReferenceHelper.GetDeclarations(fileSystem, path).Any(declaration => IsCustomSdkName(declaration.Name));

        /// <summary>
        /// Determines whether an SDK name is outside the <c>Microsoft.NET.Sdk</c> family, which ships with the .NET SDK
        /// and marks its own package references as implicitly defined.
        /// </summary>
        public static bool IsCustomSdkName(string sdkName) =>
            !string.Equals(sdkName, "Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) &&
            !sdkName.StartsWith("Microsoft.NET.Sdk.", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the names of the packages whose <c>PackageReference</c> items are all defined in a file inside one of
        /// <paramref name="packageFolders"/>. A package with at least one item defined elsewhere, for example in the
        /// project or in a <c>Directory.Build.props</c> file, is referenced by the user and is not returned.
        /// </summary>
        public static HashSet<string> GetPackageDefinedNames(IFileSystem fileSystem, IEnumerable<PackageReferenceItem> items, IEnumerable<string?> packageFolders)
        {
            var folders = packageFolders
                .Where(folder => !string.IsNullOrEmpty(folder))
                .Select(folder => fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(folder!)) + fileSystem.Path.DirectorySeparatorChar)
                .Distinct(PathComparer)
                .ToList();

            return items
                .Where(item => !string.IsNullOrEmpty(item.Name))
                .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.All(item => IsInFolder(fileSystem, item.DefiningProjectFullPath, folders)))
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Marks the direct package dependencies named in <paramref name="packageDefinedNames"/> as auto-referenced.
        /// A declared SDK with the same id as an injected package stays as it is, so its upgrade is still reported.
        /// </summary>
        /// <returns>The dependencies, in the same order, with the named ones marked.</returns>
        public static IReadOnlyList<Dependency> MarkAsAutoReferenced(IEnumerable<Dependency> dependencies, ISet<string> packageDefinedNames) =>
            dependencies
                .Select(dependency =>
                    !dependency.IsTransitive && !dependency.IsAutoReferenced && dependency.Kind == ReferenceKind.Package &&
                    packageDefinedNames.Contains(dependency.Name)
                        ? dependency with { IsAutoReferenced = true }
                        : dependency)
                .ToList();

        private static bool IsInFolder(IFileSystem fileSystem, string? path, List<string> folders) =>
            !string.IsNullOrEmpty(path) &&
            folders.Any(folder => fileSystem.Path.GetFullPath(path).StartsWith(folder, PathComparison));
    }
}
