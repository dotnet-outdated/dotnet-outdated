using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class ProjectDiscoveryServiceTests
    {
        private readonly string _path = XFS.Path(@"c:\path");
        private readonly string _solution1 = XFS.Path(@"c:\path\solution1.sln");
        private readonly string _solution2 = XFS.Path(@"c:\path\solution2.sln");
        private readonly string _project1 = XFS.Path(@"c:\path\project1.csproj");
        private readonly string _project2 = XFS.Path(@"c:\path\project2.csproj");
        private readonly string _project3 = XFS.Path(@"c:\path\project3.fsproj");
        private readonly string _nonProjectFile = XFS.Path(@"c:\path\file.cs");

        [Fact]
        public void SingleSolution_ReturnsSolution()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _solution1, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(_path);
            
            // Assert
            Assert.Equal(project, _solution1);
        }

        [Fact]
        public void MultipleSolutions_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _solution1, MockFileData.NullObject},
                { _solution2, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(_path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleSolutions, _path));
        }
        
        [Fact]
        public void SingleProject_ReturnsCsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(_path);
            
            // Assert
            Assert.Equal(project, _project1);
        }
        
        [Fact]
        public void SingleProject_ReturnsFsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project3, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(_path);
            
            // Assert
            Assert.Equal(project, _project3);
        }

        [Fact]
        public void MultipleProjects_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _project1, MockFileData.NullObject},
                { _project2, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(_path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleProjects, _path));
        }

        [Fact]
        public void NoSolutionsOrProjects_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {_nonProjectFile, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(_path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, _path));
        }
        
        [Fact]
        public void NonExistentPath_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>(), _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(_path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryOrFileDoesNotExist, _path));
        }
        
        
        [Fact]
        public void NonSolution_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {_nonProjectFile, MockFileData.NullObject}
            }, _path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(_nonProjectFile));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.FileNotAValidSolutionOrProject, _nonProjectFile));
        }
    }
}
