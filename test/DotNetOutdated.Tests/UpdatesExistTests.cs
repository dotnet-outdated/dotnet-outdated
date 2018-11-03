using NuGet.Versioning;
using System.Collections.Generic;
using DotNetOutdated.Models;
using Xunit;
using DotNetOutdated.Services;

namespace DotNetOutdated.Tests
{
    public class UpdatesExistTests
    {
        [Fact]
        public void ProjectsWithUpdates_ReturnsTrue()
        {
            // Arrange
            var dependencyWithUpdate = new Dependency
            {
                ResolvedVersion = new NuGetVersion("1.0.0"),
                LatestVersion = new NuGetVersion("1.0.1")
            };
            var dependencyWithoutUpdate = new Dependency
            {
                ResolvedVersion = new NuGetVersion("1.0.0"),
                LatestVersion = new NuGetVersion("1.0.0")
            };
            var targetFrameworkWithUpdate = new TargetFramework
            {
                Dependencies = new List<Dependency>
                {
                    dependencyWithoutUpdate,
                    dependencyWithUpdate
                }
            };
            var targetFrameworkWithoutUpdate = new TargetFramework
            {
                Dependencies = new List<Dependency>
                {
                    dependencyWithoutUpdate,
                    dependencyWithoutUpdate
                }
            };
            var projects = new List<Project>
            {
                new Project
                {
                    TargetFrameworks = new List<TargetFramework>
                    {
                        targetFrameworkWithoutUpdate,
                        targetFrameworkWithUpdate
                    }
                }
            };

            // Act
            var updatesExist = Program.UpdatesExist(projects);

            // Assert
            Assert.True(updatesExist);
        }

        [Fact]
        public void ProjectsWithoutUpdates_ReturnsFalse()
        {
            // Arrange
            var dependencyWithoutUpdate = new Dependency
            {
                ResolvedVersion = new NuGetVersion("1.0.0"),
                LatestVersion = new NuGetVersion("1.0.0")
            };
            var targetFrameworkWithoutUpdate = new TargetFramework
            {
                Dependencies = new List<Dependency>
                {
                    dependencyWithoutUpdate,
                    dependencyWithoutUpdate
                }
            };
            var projects = new List<Project>
            {
                new Project
                {
                    TargetFrameworks = new List<TargetFramework>
                    {
                        targetFrameworkWithoutUpdate,
                        targetFrameworkWithoutUpdate
                    }
                }
            };

            // Act
            var updatesExist = Program.UpdatesExist(projects);

            // Assert
            Assert.False(updatesExist);
        }
    }
}
