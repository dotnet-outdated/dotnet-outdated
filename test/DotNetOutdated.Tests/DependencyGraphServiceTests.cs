using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading.Tasks;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Resources;
using DotNetOutdated.Core.Services;
using NSubstitute;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class DependencyGraphServiceTests
    {
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _solutionPath = XFS.Path(@"c:\path\proj.sln");

        [Fact]
        public async Task SuccessfulDotNetRunnerExecutionReturnsDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default)
                .ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, exitCode: 0))
                .AndDoes(x =>
                {
                    var directory = x.ArgAt<string>(0);
                    var arguments = x.ArgAt<string[]>(1);
                    
                    ArgumentNullException.ThrowIfNull(directory);

                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[5].Replace("/p:RestoreGraphOutputPath=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Act
            var dependencyGraph = await graphService.GenerateDependencyGraphAsync(_path, string.Empty);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(3, dependencyGraph.Projects.Count);

            dotNetRunner.Received().Run(XFS.Path(@"c:\"), Arg.Is<string[]>(a => a[0] == "msbuild" && a[1] == _path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("linux-x64")]
        public async Task ProjectRestoreGraphIsGeneratedWithoutFailingOnWarnings(string runtime)
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());
            var projectPath = XFS.Path(@"c:\path\proj.csproj");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            string[] arguments = null;
            dotNetRunner.Run(default, default)
                .ReturnsForAnyArgs(new RunStatus(string.Empty, string.Empty, exitCode: 0))
                .AndDoes(x =>
                {
                    arguments = x.ArgAt<string[]>(1);
                    mockFileSystem.AddFileFromEmbeddedResource(arguments[5]["/p:RestoreGraphOutputPath=".Length..], GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            await new DependencyGraphService(dotNetRunner, mockFileSystem).GenerateDependencyGraphAsync(projectPath, runtime);

            Assert.NotNull(arguments);
            Assert.Equal(
                ["msbuild", projectPath, "/p:NoWarn=NU1605", "/p:TreatWarningsAsErrors=false", "/t:Restore,GenerateRestoreGraphFile"],
                arguments[..5]);
            Assert.StartsWith("/p:RestoreGraphOutputPath=", arguments[5], StringComparison.Ordinal);
            string[] runtimeArguments = runtime == null ? [] : ["/p:RuntimeIdentifiers=linux-x64"];
            Assert.Equal(runtimeArguments, arguments[6..]);
        }

        [Fact]
        public async Task UnsuccessfulDotNetRunnerExecutionThrows()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(Arg.Any<string>(), Arg.Any<string[]>())
                .Returns(new RunStatus(string.Empty, string.Empty, 1));

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Assert
            await Assert.ThrowsAsync<CommandValidationException>(() => graphService.GenerateDependencyGraphAsync(_path, string.Empty));
        }

        [Theory]
        [InlineData(@"c:\path\proj.csproj", "Are you sure this is a valid .NET Core or .NET Standard project type?")]
        [InlineData(@"c:\path\proj.sln", "Are you sure this is a valid .NET Core or .NET Standard project type?")]
        [InlineData(@"c:\path\app.cs", "Are you sure this is a valid .NET file-based app? File-based app support requires .NET SDK 10.0.300 or later")]
        public async Task UnsuccessfulDotNetRunnerExecutionExplainsWhatKindOfProjectWasExpected(string projectPath, string expectedHint)
        {
            var path = XFS.Path(projectPath);
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(Arg.Any<string>(), Arg.Any<string[]>())
                .Returns(new RunStatus("the output", "the errors", 1));

            var graphService = new DependencyGraphService(dotNetRunner, new MockFileSystem());

            var exception = await Assert.ThrowsAsync<CommandValidationException>(() => graphService.GenerateDependencyGraphAsync(path, string.Empty));

            Assert.StartsWith($"Unable to process the project `{path}`. {expectedHint}{Environment.NewLine}", exception.Message, StringComparison.Ordinal);
            Assert.EndsWith("the output - the errors - exit code: 1", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("linux-x64", true)]
        public async Task FileBasedAppPassesRuntimeIdentifiersToRestoreAndBuild(string runtime, bool expectRuntimeArgument)
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());
            var appPath = XFS.Path(@"c:\path\app.cs");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "restore"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0));
            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "build"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .AndDoes(x =>
                {
                    var arguments = x.ArgAt<string[]>(1);
                    string tempFileName = arguments[6].Replace("/p:RestoreGraphOutputPath=", string.Empty, StringComparison.OrdinalIgnoreCase);
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            await graphService.GenerateDependencyGraphAsync(appPath, runtime);

            string[] runtimeArguments = expectRuntimeArgument ? ["/p:RuntimeIdentifiers=linux-x64"] : [];
            dotNetRunner.Received(1).Run(_path, Arg.Is<string[]>(a => a[0] == "restore" && a.Skip(2).SequenceEqual(runtimeArguments)));
            dotNetRunner.Received(1).Run(_path, Arg.Is<string[]>(a => a[0] == "build" && a.Skip(7).SequenceEqual(runtimeArguments)));
        }

        [Fact]
        public void TheDefaultPackageReferenceItemsAreEmptyRatherThanAFailedEvaluation()
        {
            IDependencyGraphService graphService = new GraphOnlyDependencyGraphService();

            var items = graphService.GetPackageReferenceItems(XFS.Path(@"c:\path\proj.csproj"), "net8.0");

            Assert.NotNull(items);
            Assert.Empty(items);
        }

        private sealed class GraphOnlyDependencyGraphService : IDependencyGraphService
        {
            public Task<NuGet.ProjectModel.DependencyGraphSpec> GenerateDependencyGraphAsync(string projectPath, string runtime) =>
                Task.FromResult(new NuGet.ProjectModel.DependencyGraphSpec());
        }

        [Fact]
        public async Task EmptyGraphFileReturnsEmptyDependencyGraph()
        {
            // Covers MSBuild writing a restore graph that lists no projects. Current SDKs (8.0 to 11.0)
            // write no file at all for an empty solution; that case is covered by
            // SolutionWithoutGraphFileThrowsNoRestorableProjectsMessage.
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = Substitute.For<IDotNetRunner>();

            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "msbuild" && a[4] == "/t:Restore,GenerateRestoreGraphFile"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .AndDoes(x =>
                {
                    var directory = x.ArgAt<string>(0);
                    var arguments = x.ArgAt<string[]>(1);

                    ArgumentNullException.ThrowIfNull(directory);

                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[5].Replace("/p:RestoreGraphOutputPath=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.empty.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Act
            var dependencyGraph = await graphService.GenerateDependencyGraphAsync(_solutionPath, string.Empty);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Empty(dependencyGraph.Projects);

            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a => a[0] == "msbuild" && a[1] == _solutionPath && a[4] == "/t:Restore,GenerateRestoreGraphFile"));
        }

        [Theory]
        [InlineData(@"c:\path\proj.sln")]
        [InlineData(@"c:\path\proj.slnx")]
        [InlineData(@"c:\path\proj.slnf")]
        public async Task SolutionWithoutGraphFileThrowsNoRestorableProjectsMessage(string solutionPath)
        {
            // MSBuild reports success but never writes the restore graph file, as happens when the
            // solution or solution filter selects no restorable projects (issue #747).
            var path = XFS.Path(solutionPath);
            var graphService = new DependencyGraphService(SucceedingRunnerThatWritesNothing(), new MockFileSystem());

            var exception = await Assert.ThrowsAsync<CommandValidationException>(() => graphService.GenerateDependencyGraphAsync(path, string.Empty));

            Assert.Equal(string.Format(CultureInfo.InvariantCulture, ValidationErrorMessages.NoRestorableProjectsInSolution, path), exception.Message);
        }

        [Theory]
        [InlineData(@"c:\path\proj.csproj")]
        [InlineData(@"c:\path\proj.fsproj")]
        [InlineData(@"c:\path\app.cs")]
        public async Task ProjectWithoutGraphFileThrowsNoDependencyGraphMessage(string projectPath)
        {
            // A project or file-based app is never "empty", so the message must not claim it has no projects.
            var path = XFS.Path(projectPath);
            var graphService = new DependencyGraphService(SucceedingRunnerThatWritesNothing(), new MockFileSystem());

            var exception = await Assert.ThrowsAsync<CommandValidationException>(() => graphService.GenerateDependencyGraphAsync(path, string.Empty));

            Assert.Equal(string.Format(CultureInfo.InvariantCulture, ValidationErrorMessages.NoDependencyGraphForProject, path), exception.Message);
        }

        private static IDotNetRunner SucceedingRunnerThatWritesNothing()
        {
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(Arg.Any<string>(), Arg.Any<string[]>())
                .Returns(new RunStatus(string.Empty, string.Empty, exitCode: 0));
            return dotNetRunner;
        }

        [Fact]
        public async Task FileBasedAppGeneratesDependencyGraphWithRestoreThenBuild()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());
            var appPath = XFS.Path(@"c:\path\app.cs");

            // Arrange
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "restore"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0));
            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "build"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .AndDoes(x =>
                {
                    var arguments = x.ArgAt<string[]>(1);
                    string tempFileName = arguments[6].Replace("/p:RestoreGraphOutputPath=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('"');
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Act
            var dependencyGraph = await graphService.GenerateDependencyGraphAsync(appPath, string.Empty);

            // Assert
            Assert.NotNull(dependencyGraph);
            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a => a[0] == "restore" && a[1] == appPath));
            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a => a[0] == "build" && a[1] == appPath && a[2] == "--no-restore" && a[5] == "/t:GenerateRestoreGraphFile"));
        }

        [Fact]
        public void GetPackageReferenceItemsReturnsItemsWithDefiningFile()
        {
            var projectPath = XFS.Path(@"c:\path\proj.csproj");
            var output = """
                {
                  "Items": {
                    "PackageReference": [
                      {
                        "Identity": "MSTest.TestFramework",
                        "DefiningProjectFullPath": "C:\\packages\\mstest.sdk\\3.6.0\\Sdk\\Runner\\ClassicEngine.targets"
                      },
                      {
                        "Identity": "Newtonsoft.Json",
                        "Version": "11.0.1",
                        "DefiningProjectFullPath": "C:\\path\\proj.csproj"
                      }
                    ]
                  }
                }
                """;

            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(output, string.Empty, exitCode: 0));

            var graphService = new DependencyGraphService(dotNetRunner, new MockFileSystem());

            var items = graphService.GetPackageReferenceItems(projectPath, "net8.0");

            Assert.Equal(
                [
                    new PackageReferenceItem("MSTest.TestFramework", @"C:\packages\mstest.sdk\3.6.0\Sdk\Runner\ClassicEngine.targets"),
                    new PackageReferenceItem("Newtonsoft.Json", @"C:\path\proj.csproj")
                ],
                items);
            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a =>
                a.Length == 4 && a[0] == "msbuild" && a[1] == projectPath && a[2] == "-getItem:PackageReference" && a[3] == "-p:TargetFramework=net8.0"));
        }

        [Fact]
        public void GetPackageReferenceItemsPassesRuntimeIdentifiers()
        {
            var projectPath = XFS.Path(@"c:\path\proj.csproj");
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus("{}", string.Empty, exitCode: 0));

            var graphService = new DependencyGraphService(dotNetRunner, new MockFileSystem());

            graphService.GetPackageReferenceItems(projectPath, "net8.0", "linux-x64");

            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a =>
                a.Length == 5 && a[2] == "-getItem:PackageReference" && a[3] == "-p:TargetFramework=net8.0" && a[4] == "-p:RuntimeIdentifiers=linux-x64"));
        }

        [Theory]
        [InlineData("not json", 0)]
        [InlineData("{ \"Items\": { \"PackageReference\": [] } }\nTrailing text", 0)]
        [InlineData("{ \"Items\": { \"PackageReference\": [ { \"Version\": \"1.0.0\" } ] } }", 0)]
        [InlineData("[]", 0)]
        [InlineData("", 1)]
        public void GetPackageReferenceItemsReturnsNullWhenEvaluationFails(string output, int exitCode)
        {
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(output, string.Empty, exitCode));

            var graphService = new DependencyGraphService(dotNetRunner, new MockFileSystem());

            Assert.Null(graphService.GetPackageReferenceItems(XFS.Path(@"c:\path\proj.csproj"), "net8.0"));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{ \"Items\": {} }")]
        [InlineData("{ \"Items\": { \"PackageReference\": [] } }")]
        public void GetPackageReferenceItemsReturnsEmptyWhenThereAreNoItems(string output)
        {
            var dotNetRunner = Substitute.For<IDotNetRunner>();
            dotNetRunner.Run(default, default).ReturnsForAnyArgs(new RunStatus(output, string.Empty, exitCode: 0));

            var graphService = new DependencyGraphService(dotNetRunner, new MockFileSystem());

            Assert.Empty(graphService.GetPackageReferenceItems(XFS.Path(@"c:\path\proj.csproj"), "net8.0"));
        }
    }
}
