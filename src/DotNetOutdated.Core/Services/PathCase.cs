using System;
using System.IO;
using System.IO.Abstractions;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Spells paths as the file system stores them, so two paths name the same file exactly when they compare equal
/// ordinally. Whether case matters depends on the volume (macOS and Windows can each have case-sensitive ones), so it
/// is asked of the file system rather than assumed from the operating system.
/// </summary>
internal static class PathCase
{
    /// <summary>
    /// The full path of <paramref name="path"/>, each part of it that exists spelled as its directory lists it. A part
    /// whose spelling differs only in case is the stored one only where the file system finds it as written, that is
    /// where case doesn't matter; the parts from the first one that doesn't exist are kept as written.
    /// </summary>
    public static string OnDisk(IFileSystem fileSystem, string path)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        var root = fileSystem.Path.GetPathRoot(fullPath) ?? string.Empty;
        var segments = fullPath[root.Length..].Split(
            [fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // No directory lists the root. On Windows its parts (a drive letter, or a UNC server and share, also behind
        // \\?\) name the same place whatever their case, so it is spelled in upper case.
        var current = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        for (var i = 0; i < segments.Length; i++)
        {
            if (StoredName(fileSystem, current, segments[i]) is not { } name)
            {
                return fileSystem.Path.Combine([current, .. segments[i..]]);
            }

            current = fileSystem.Path.Combine(current, name);
        }

        return current;
    }

    // The name the directory lists for the entry written as name, or null when the file system doesn't find it.
    private static string? StoredName(IFileSystem fileSystem, string directory, string name)
    {
        try
        {
            string? differingInCase = null;
            foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(directory))
            {
                var entryName = fileSystem.Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.Ordinal))
                {
                    return entryName;
                }

                if (differingInCase == null && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                {
                    differingInCase = entryName;
                }
            }

            // Where case matters, a name that differs only in case is another file, and the one written doesn't exist.
            var written = fileSystem.Path.Combine(directory, name);
            return differingInCase != null && (fileSystem.File.Exists(written) || fileSystem.Directory.Exists(written))
                ? differingInCase
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
