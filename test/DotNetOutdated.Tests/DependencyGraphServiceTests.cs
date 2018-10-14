using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using Moq;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class DependencyGraphServiceTests
    {
        private const string Path = @"c:\path";
        private const string SolutionPath = @"c:\path\proj.sln";
        private const string Project1Path = @"c:\path\proj1\proj1.csproj";
        private const string Project2Path = @"c:\path\proj2\proj2.csproj";

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
            var dependencyGraph = graphService.GenerateDependencyGraph(Path);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(3, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(@"c:\", It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + Path + '\"')));
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
            Assert.Throws<CommandValidationException>(() => graphService.GenerateDependencyGraph(Path));
        }

        [Fact]
        public void SolutionPath_ReturnsDependencyGraphForAllProjects()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            string solutionProjects = string.Join(Environment.NewLine, "Project(s)", "-----------", Project1Path, Project2Path);
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "sln")))
                .Returns(new RunStatus(solutionProjects, string.Empty, 0));

            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    string dependencyGraphFile = arguments[1] == '\"' + Project1Path + '\"' ? "test.dg" : "empty.dg";
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData." + dependencyGraphFile);
                });

            mockFileSystem.AddFileFromEmbeddedResource(Project1Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");
            mockFileSystem.AddFileFromEmbeddedResource(Project2Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(SolutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(4, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(Path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + SolutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(@"c:\path\proj1", It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + Project1Path + '\"')));
            dotNetRunner.Verify(runner => runner.Run(@"c:\path\proj2", It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + Project2Path + '\"')));
        }

        [Fact]
        public void SolutionPathWithLegacyProject_ReturnsDependencyGraphForSingleProjects()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = new Mock<IDotNetRunner>();

            string solutionProjects = string.Join(Environment.NewLine, "Project(s)", "-----------", Project1Path, Project2Path);
            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "sln")))
                .Returns(new RunStatus(solutionProjects, string.Empty, 0));

            dotNetRunner.Setup(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .Callback((string directory, string[] arguments) =>
                {
                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[3].Replace("/p:RestoreGraphOutputPath=", string.Empty).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    string dependencyGraphFile = arguments[1] == '\"' + Project1Path + '\"' ? "test.dg" : "empty.dg";
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData." + dependencyGraphFile);
                });

            mockFileSystem.AddFileFromEmbeddedResource(Project1Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.LegacySdk.xml");
            mockFileSystem.AddFileFromEmbeddedResource(Project2Path, GetType().Assembly, "DotNetOutdated.Tests.TestData.MicrosoftSdk.xml");

            var graphService = new DependencyGraphService(dotNetRunner.Object, mockFileSystem);

            // Act
            var dependencyGraph = graphService.GenerateDependencyGraph(SolutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(1, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(Path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + SolutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(@"c:\path\proj2", It.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + Project2Path + '\"')));
            dotNetRunner.Verify(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild" && a[1] != '\"' + Project2Path + '\"')), Times.Never());
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
            var dependencyGraph = graphService.GenerateDependencyGraph(SolutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(0, dependencyGraph.Projects.Count);

            dotNetRunner.Verify(runner => runner.Run(Path, It.Is<string[]>(a => a[0] == "sln" && a[2] == "list" && a[1] == '\"' + SolutionPath + '\"')));
            dotNetRunner.Verify(runner => runner.Run(It.IsAny<string>(), It.Is<string[]>(a => a[0] == "msbuild")), Times.Never());
        }
    }
}