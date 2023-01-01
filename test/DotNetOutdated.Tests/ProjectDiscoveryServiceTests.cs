using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Resources;
using DotNetOutdated.Core.Services;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class ProjectDiscoveryServiceTests
    {
        private readonly string _nonProjectFile = XFS.Path(@"c:\path\file.cs");
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _project1 = XFS.Path(@"c:\path\project1.csproj");
        private readonly string _project2 = XFS.Path(@"c:\path\project2.csproj");
        private readonly string _project3 = XFS.Path(@"c:\path\project3.fsproj");
        private readonly string _project4 = XFS.Path(@"c:\path\sub\project4.csproj");
        private readonly string _solution1 = XFS.Path(@"c:\path\solution1.sln");
        private readonly string _solution2 = XFS.Path(@"c:\path\solution2.sln");
        private readonly string _solutionFilter1 = XFS.Path(@"c:\path\solution1.slnf");
        private readonly string _someOtherPath = XFS.Path(@"c:\another_path");

        [Fact]
        public void CanCreateFiles1()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, Singletons.NullObject},
                { _project3, Singletons.NullObject},
                { _project4, Singletons.NullObject}
            }, _path);

            // Act

            // Assert
            Assert.True(fileSystem.File.GetAttributes(_path).HasFlag(FileAttributes.Directory));

            var projects = fileSystem.Directory.GetFiles(_path, "*.csproj", SearchOption.AllDirectories)
                .ToList();
            Assert.Equal(2, projects.Count);
        }

        [Fact]
        public void CanCreateFiles2()
        {
            // Arrange
            var fileSystem = new MockFileSystem();
            fileSystem.AddFile(_project1, Singletons.NullObject);
            fileSystem.AddFile(_project2, Singletons.NullObject);
            fileSystem.AddFile(_project3, Singletons.NullObject);

            // Act

            // Assert
            Assert.True(fileSystem.File.GetAttributes(_path).HasFlag(FileAttributes.Directory));

            var projects = fileSystem.Directory.GetFiles(_path, "*.csproj", SearchOption.AllDirectories)
                .ToList();
            Assert.Equal(2, projects.Count);
        }

        [Fact]
        public void MultipleProjectsRecursiveReturnsProjects()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, Singletons.NullObject},
                { _project3, Singletons.NullObject},
                { _project4, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            var projects = projectDiscoveryService.DiscoverProjects(_path, true);
            Assert.Equal(3, projects.Count);
        }

        [Fact]
        public void MultipleProjectsThrows()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, Singletons.NullObject},
                { _project2, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProjects(_path));
        }

        [Fact]
        public void MultipleSolutionsThrows()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _solution1, Singletons.NullObject},
                { _solution2, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProjects(_path));
        }

        [Fact]
        public void NonExistentPathThrows()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>(), _someOtherPath);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProjects(_path));
            Assert.Equal(string.Format(CultureInfo.InvariantCulture, ValidationErrorMessages.DirectoryOrFileDoesNotExist, _path), exception.Message);
        }

        [Fact]
        public void NonSolutionThrows()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {_nonProjectFile, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProjects(_nonProjectFile));
        }

        [Fact]
        public void NoSolutionsOrProjectsThrows()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>(), _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act

            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProjects(_path));
            Assert.Equal(string.Format(CultureInfo.InvariantCulture, ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, _path), exception.Message);
        }

        [Fact]
        public void SingleProjectReturnsCsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act
            string project = projectDiscoveryService.DiscoverProjects(_path).Single();

            // Assert
            Assert.Equal(_project1, project);
        }

        [Fact]
        public void SingleProjectReturnsFsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project3, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act
            string project = projectDiscoveryService.DiscoverProjects(_path).Single();

            // Assert
            Assert.Equal(_project3, project);
        }

        [Fact]
        public void SingleSolutionFilterReturnsSolution()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _solutionFilter1, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act
            var projects = projectDiscoveryService.DiscoverProjects(_path);

            // Assert
            Assert.Single(projects);
            Assert.Equal(_solutionFilter1, projects[0]);
        }

        [Fact]
        public void SingleSolutionReturnsSolution()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _solution1, Singletons.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);

            // Act
            string project = projectDiscoveryService.DiscoverProjects(_path).Single();

            // Assert
            Assert.Equal(_solution1, project);
        }
    }
}
