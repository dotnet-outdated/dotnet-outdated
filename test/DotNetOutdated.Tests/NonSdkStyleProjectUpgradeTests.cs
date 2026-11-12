using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests;

/// <summary>A project that isn't SDK-style has a package removed before it is added back at the new version.</summary>
public static class NonSdkStyleProjectUpgradeTests
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
            <Project>
              <Sdk Name="Microsoft.Build.NoTargets" Version="3.7.0" />
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.Build.NoTargets" Version="3.7.0" />
              <Import Project="Sdk.targets" Sdk="Microsoft.Build.NoTargets" Version="3.7.0" />
            </Project>
            """,
            true);

        testCases.Add(
            """
            <Project>
              <Import Project="Directory.Build.targets" />
            </Project>
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
    public static void Upgrade_RemovesThePackageFirstOnlyFromAProjectThatIsNotSdkStyle(string contents, bool sdkStyle)
    {
        var projectPath = XFS.Path(@"c:\repo\App\App.csproj");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new(contents) });
        var dotNetRunner = Substitute.For<IDotNetRunner>();
        dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "", 0));

        var result = new ReferenceUpgrader(dotNetRunner, fileSystem).Upgrade(projectPath, "Foo", "net8.0", new NuGetVersion("2.0.0"));

        Assert.True(result.IsSuccess);
        dotNetRunner.Received(sdkStyle ? 0 : 1).Run(
            XFS.Path(@"c:\repo\App"), Arg.Is<string[]>(a => a.SequenceEqual(new[] { "remove", "App.csproj", "package", "Foo" })));
        dotNetRunner.Received(1).Run(XFS.Path(@"c:\repo\App"), Arg.Is<string[]>(a => a[0] == "add" && a[3] == "Foo"));
        Assert.Equal(sdkStyle ? 0 : 1, result.Warnings.Count);
    }

    [Fact]
    public static void Upgrade_StopsWhenRemovingThePackageFails()
    {
        var projectPath = XFS.Path(@"c:\repo\App\App.csproj");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [projectPath] = new("<Project ToolsVersion=\"15.0\"></Project>") });
        var dotNetRunner = Substitute.For<IDotNetRunner>();
        dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("", "could not remove", 1));

        var result = new ReferenceUpgrader(dotNetRunner, fileSystem).Upgrade(projectPath, "Foo", "net8.0", new NuGetVersion("2.0.0"));

        Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("could not remove", result.FailureDetails);
        dotNetRunner.ReceivedWithAnyArgs(1).Run(default, default);
    }
}
