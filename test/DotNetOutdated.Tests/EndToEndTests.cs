using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace DotNetOutdated.Tests;

public class EndToEndTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory;

    public EndToEndTests()
    {
        _tempDirectory = new TemporaryDirectory();
    }

    public void Dispose()
    {
        _tempDirectory.Dispose();
    }

    [Theory]
    [InlineData("build-props")]
    [InlineData("development-dependencies")]
    [InlineData("multi-target", Skip = "Fails on Windows in GitHub Actions for some reason.")]
    public void Can_Upgrade_Project(string testProjectName)
    {
        TestSetup(testProjectName);

        var actual = Program.Main([_tempDirectory.Path]);
        Assert.Equal(0, actual);
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Markdown)]
    public void All_Formatters_Succeed(OutputFormat format)
    {
        TestSetup("development-dependencies");

        var outputPath = Path.Combine(_tempDirectory.Path, "output");

        var actual = Program.Main([_tempDirectory.Path, "--output", outputPath, "--output-format", format.ToString()]);
        Assert.Equal(0, actual);
    }

    private void TestSetup(string testProjectName)
    {
        var solutionRoot = typeof(EndToEndTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First((p) => p.Key is "SolutionRoot")
            .Value;

        var projectPath = Path.Combine(solutionRoot, "test-projects", testProjectName);

        foreach (var source in Directory.GetFiles(projectPath, "*", SearchOption.TopDirectoryOnly))
        {
            string destination = Path.Combine(_tempDirectory.Path, Path.GetFileName(source));
            File.Copy(source, destination);
        }
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
#if NET6_0
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            return Directory.CreateDirectory(tempPath);
#else
            return Directory.CreateTempSubdirectory(Prefix);
#endif
        }
    }
}