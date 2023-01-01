using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Services;
using Moq;
using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class DependencyGraphServiceTests
    {
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _solutionPath = XFS.Path(@"c:\path\proj.sln");
        //private readonly string _project1Path = XFS.Path(@"c:\path\proj1\proj1.csproj");
        //private readonly string _project2Path = XFS.Path(@"c:\path\proj2\proj2.csproj");

        [Fact]
        public void SuccessfulDotNetRunnerExecutionReturnsDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.IsAny<string[]>()))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    ArgumentNullException.ThrowIfNull(directory);

                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty, System.StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_path);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(3, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(XFS.Path(@"c:\"), It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _path + '\"')));
        }

        [Fact]
        public void UnsuccessfulDotNetRunnerExecutionThrows()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.IsAny<string[]>()))
                .Returns(new RunStatus(string.Empty, string.Empty, 1));

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Assert
            Assert.Throws<CommandValidationException>(() => graphService.GenerateDependencyGraph(_path));
        }

        [Fact]
        public void EmptySolutionReturnsEmptyDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild" && a[2] == "/t:Restore,GenerateRestoreGraphFile")))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    ArgumentNullException.ThrowIfNull(directory);

                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty, System.StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.empty.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_solutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(0, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(_path, It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _solutionPath + '\"' && a[2] == "/t:Restore,GenerateRestoreGraphFile")));
        }
    }
}
