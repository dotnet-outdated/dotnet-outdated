using System;
using System.IO;

namespace DotNetOutdated.Core
{
    public static class ProjectPathExtensions
    {
        public static bool IsCSharpFile(this string path)
        {
            return string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCSharpProjectFile(this string path)
        {
            return string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsProjectFile(this string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSolutionFile(this string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".slnf", StringComparison.OrdinalIgnoreCase);
        }
    }
}
