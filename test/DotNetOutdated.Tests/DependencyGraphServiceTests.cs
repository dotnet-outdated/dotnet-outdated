using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Services;
using Moq;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class DependencyGraphServiceTests
    {
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _solutionPath = XFS.Path(@"c:\path\proj.sln");
        private readonly string _project1Path = XFS.Path(@"c:\path\proj1\proj1.csproj");
        private readonly string _project2Path = XFS.Path(@"c:\path\proj2\proj2.csproj");

        [Fact]
        public void SuccessfulDotNetRunnerExecution_ReturnsDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.IsAny<string[]>()))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });
            
            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);
            
            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_path);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(3, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(XFS.Path(@"c:\", null), It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _path + '\"')));
        }
        
        [Fact]
        public void UnsuccessfulDotNetRunnerExecution_Throws()
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
        public void SolutionPath_ReturnsDependencyGraphForAllProjects()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            string solutionProjects = string.Join(Environment.NewLine, "Project(s)", "-----------", _project1Path, _project2Path);
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "sln")))
                .Returns(new RunStatus(solutionProjects, string.Empty, 0));

            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    string dependencyGraphFile = arguments[1] == '\"' + _project1Path + '\"' ? "test.dg" : "empty.dg";
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData." + dependencyGraphFile);
                });

            mockFileSystem.AddFileFromEmbeddedResource(_project1Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");
            mockFileSystem.AddFileFromEmbeddedResource(_project2Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_solutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(4, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(_path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + _solutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(XFS.Path(@"c:\path\proj1", null), It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _project1Path + '\"')));
            dotNetRunner.Verify(runner => runner.Run(XFS.Path(@"c:\path\proj2", null), It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _project2Path + '\"')));
        }

        [Fact]
        public void SolutionPathWithLegacyProject_ReturnsDependencyGraphForSingleProjects()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            string solutionProjects = string.Join(Environment.NewLine, "Project(s)", "-----------", _project1Path, _project2Path);
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "sln")))
                .Returns(new RunStatus(solutionProjects, string.Empty, 0));

            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    string dependencyGraphFile = arguments[1] == '\"' + _project1Path + '\"' ? "test.dg" : "empty.dg";
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData." + dependencyGraphFile);
                });

            mockFileSystem.AddFileFromEmbeddedResource(_project1Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.LegacySdk.xml");
            mockFileSystem.AddFileFromEmbeddedResource(_project2Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_solutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(1, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(_path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + _solutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(XFS.Path(@"c:\path\proj2", null), It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _project2Path + '\"')));
            dotNetRunner.Verify(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild" && a[1] != '\"' + _project2Path + '\"')), Times.Never());
        }

        [Fact]
        public void EmptySolution_ReturnsEmptyDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            string solutionProjects = string.Join(Environment.NewLine, "Project(s)", "-----------");
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "sln")))
                .Returns(new RunStatus(solutionProjects, string.Empty, 0));

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(_solutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(0, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(_path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + _solutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")), Times.Never());
        }
    }
}