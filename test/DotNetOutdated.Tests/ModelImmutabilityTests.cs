#nullable enable

using System;
using System.Collections.Generic;
using DotNetOutdated.Core.Models;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class ModelImmutabilityTests
    {
        private static readonly Dependency Package = new() { Name = "Package", VersionRange = VersionRange.Parse("1.0.0") };

        [Fact]
        public void ChangingTheListAModelWasCreatedFrom_DoesNotChangeTheModel()
        {
            var sources = new List<Uri> { new("https://api.nuget.org/v3/index.json") };
            var dependencies = new List<Dependency> { Package };
            var targetFrameworks = new List<TargetFramework> { new() { Name = NuGetFramework.Parse("net9.0"), Dependencies = dependencies } };
            var project = new Project { Name = "App", FilePath = "/App.csproj", Version = new NuGetVersion("1.0.0"), Sources = sources, TargetFrameworks = targetFrameworks };

            sources.Clear();
            dependencies.Clear();
            targetFrameworks.Clear();

            Assert.Single(project.Sources);
            Assert.Single(Assert.Single(project.TargetFrameworks).Dependencies);
        }

        [Fact]
        public void AModelsListsCannotBeChangedThroughACast()
        {
            // Analysis builds models from List<T>s, which a cast to IList<T> could otherwise change.
            var targetFramework = new TargetFramework { Name = NuGetFramework.Parse("net9.0"), Dependencies = new List<Dependency> { Package } };
            var project = new Project
            {
                Name = "App",
                FilePath = "/App.csproj",
                Version = new NuGetVersion("1.0.0"),
                TargetFrameworks = new List<TargetFramework> { targetFramework },
            };
            var outdated = new OutdatedTargetFramework
            {
                Name = targetFramework.Name,
                Dependencies = new List<OutdatedDependency> { new() { Dependency = Package } },
            };

            Assert.Throws<NotSupportedException>(() => ((IList<TargetFramework>)project.TargetFrameworks).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<Dependency>)targetFramework.Dependencies).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<OutdatedDependency>)outdated.Dependencies).Clear());
        }
    }
}
