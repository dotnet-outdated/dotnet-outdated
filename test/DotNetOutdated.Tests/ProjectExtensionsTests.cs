using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DotNetOutdated.Tests;

public static class ProjectExtensionsTests
{
    public static TheoryData<string, bool> ProjectContents()
    {
        var testCases = new TheoryData<string, bool>();

        testCases.Add(
            """
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project Sdk="MSTest.Sdk">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" Sdk="Microsoft.NET.Sdk">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" Sdk="Microsoft.NET.Sdk">
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project>
            </Project>
            """,
            false);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project>
            </Project>
            """,
            false);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="14.0">
            </Project>
            """,
            false);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
            </Project>
            """,
            false);

        testCases.Add(
            """
            <Foo></Foo>
            """,
            false);

        testCases.Add(
            """
            <Foo Sdk="Microsoft.NET.Sdk"></Foo>
            """,
            false);

        testCases.Add(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Foo>
            </Foo>
            """,
            false);

        testCases.Add(
            """
            Not XML
            """,
            false);

        return testCases;
    }

    [Theory]
    [MemberData(nameof(ProjectContents))]
    public static async Task IsProjectSdkStyle_Returns_Correct_Value(string contents, bool expected)
    {
        // Arrange
        using var file = new TemporaryFile();
        var project = new PackageProjectReference(file.Path);

        await File.WriteAllTextAsync(file.Path, contents);

        // Act
        var actual = project.IsProjectSdkStyle();

        // Assert
        Assert.Equal(expected, actual);
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception)
            {
                // Ignore
            }
        }
    }
}
