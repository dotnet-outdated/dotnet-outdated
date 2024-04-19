﻿using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Services;
using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using NSubstitute;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;
using System.Threading.Tasks;

namespace DotNetOutdated.Tests
{
    public class DependencyGraphServiceTests
    {
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _solutionPath = XFS.Path(@"c:\path\proj.sln");
        //private readonly string _project1Path = XFS.Path(@"c:\path\proj1\proj1.csproj");
        //private readonly string _project2Path = XFS.Path(@"c:\path\proj2\proj2.csproj");

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
                    string tempFileName = arguments[4].Replace("/p:RestoreGraphOutputPath=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.test.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Act
            var dependencyGraph = await graphService.GenerateDependencyGraphAsync(_path);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Equal(3, dependencyGraph.Projects.Count);

            dotNetRunner.Received().Run(XFS.Path(@"c:\"), Arg.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _path + '\"'));
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
            await Assert.ThrowsAsync<CommandValidationException>(() => graphService.GenerateDependencyGraphAsync(_path));
        }

        [Fact]
        public async Task EmptySolutionReturnsEmptyDependencyGraph()
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            // Arrange
            var dotNetRunner = Substitute.For<IDotNetRunner>();

            dotNetRunner.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a[0] == "msbuild" && a[3] == "/t:Restore,GenerateRestoreGraphFile"))
                .Returns(new RunStatus(string.Empty, string.Empty, 0))
                .AndDoes(x =>
                {
                    var directory = x.ArgAt<string>(0);
                    var arguments = x.ArgAt<string[]>(1);

                    ArgumentNullException.ThrowIfNull(directory);

                    // Grab the temp filename that was passed...
                    string tempFileName = arguments[4].Replace("/p:RestoreGraphOutputPath=", string.Empty, System.StringComparison.OrdinalIgnoreCase).Trim('"');

                    // ... and stuff it with our dummy dependency graph
                    mockFileSystem.AddFileFromEmbeddedResource(tempFileName, GetType().Assembly, "DotNetOutdated.Tests.TestData.empty.dg");
                });

            var graphService = new DependencyGraphService(dotNetRunner, mockFileSystem);

            // Act
            var dependencyGraph = await graphService.GenerateDependencyGraphAsync(_solutionPath);

            // Assert
            Assert.NotNull(dependencyGraph);
            Assert.Empty(dependencyGraph.Projects);

            dotNetRunner.Received().Run(_path, Arg.Is<string[]>(a => a[0] == "msbuild" && a[1] == '\"' + _solutionPath + '\"' && a[3] == "/t:Restore,GenerateRestoreGraphFile"));
        }
    }
}
