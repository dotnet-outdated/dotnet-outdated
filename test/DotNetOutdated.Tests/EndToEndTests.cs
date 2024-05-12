using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace DotNetOutdated.Tests;

public static class EndToEndTests
{
    [Theory]
    [InlineData("build-props")]
    [InlineData("development-dependencies")]
    [InlineData("multi-target")]
    public static void Can_Upgrade_Project(string name)
    {
        var solutionRoot = typeof(EndToEndTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First((p) => p.Key is "SolutionRoot")
            .Value;

        var projectPath = Path.Combine(solutionRoot, "test-projects", name);

        using var temp = new TemporaryDirectory();

        foreach (var source in Directory.GetFiles(projectPath, "*", SearchOption.TopDirectoryOnly))
        {
            string destination = Path.Combine(temp.Path, Path.GetFileName(source));
            File.Copy(source, destination);
        }

        var actual = Program.Main([temp.Path]);
        Assert.Equal(0, actual);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = CreateTempSubdirectory();

        public string Path => _directory.FullName;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        public bool Exists() => _directory.Exists;

        private static DirectoryInfo CreateTempSubdirectory()
        {
            const string Prefix = "dotnet-bumper-";
#if NET6_0_OR_GREATER
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            return Directory.CreateDirectory(tempPath);
#else
            return Directory.CreateTempSubdirectory(Prefix);
#endif
        }
    }
}
