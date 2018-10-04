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
        private string Path = XFS.Path(@"c:\path");
        private string Solution1 = XFS.Path(@"c:\path\solution1.sln");
        private string Solution2 = XFS.Path(@"c:\path\solution2.sln");
        private string Project1 = XFS.Path(@"c:\path\project1.csproj");
        private string Project2 = XFS.Path(@"c:\path\project2.csproj");
        private string Project3 = XFS.Path(@"c:\path\project3.fsproj");
        private string NonProjectFile = XFS.Path(@"c:\path\file.cs");

        [Fact]
        public void SingleSolution_ReturnsSolution()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { Solution1, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(Path);
            
            // Assert
            Assert.Equal(project, Solution1);
        }

        [Fact]
        public void MultipleSolutions_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { Solution1, MockFileData.NullObject},
                { Solution2, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(Path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleSolutions, Path));
        }
        
        [Fact]
        public void SingleProject_ReturnsCsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { Project1, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(Path);
            
            // Assert
            Assert.Equal(project, Project1);
        }
        
        [Fact]
        public void SingleProject_ReturnsFsProject()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { Project3, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            string project = projectDiscoveryService.DiscoverProject(Path);
            
            // Assert
            Assert.Equal(project, Project3);
        }

        [Fact]
        public void MultipleProjects_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { Project1, MockFileData.NullObject},
                { Project2, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(Path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryContainsMultipleProjects, Path));
        }

        [Fact]
        public void NoSolutionsOrProjects_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {NonProjectFile, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(Path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryDoesNotContainSolutionsOrProjects, Path));
        }
        
        [Fact]
        public void NonExistentPath_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>(), Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(Path));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.DirectoryOrFileDoesNotExist, Path));
        }
        
        
        [Fact]
        public void NonSolution_Throws()
        {
            // Arrange
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                {NonProjectFile, MockFileData.NullObject}
            }, Path);
            var projectDiscoveryService = new ProjectDiscoveryService(fileSystem);
            
            // Act
            
            // Assert
            var exception = Assert.Throws<CommandValidationException>(() => projectDiscoveryService.DiscoverProject(NonProjectFile));
            Assert.Equal(exception.Message, string.Format(Resources.ValidationErrorMessages.FileNotAValidSolutionOrProject, NonProjectFile));
        }
    }
}
