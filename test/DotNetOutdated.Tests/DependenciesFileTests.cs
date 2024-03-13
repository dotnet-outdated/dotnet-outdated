using DotNetOutdated.Core.Services;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using NSubstitute;
using Xunit;
using NuGet.Versioning;
using System;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Reflection.PortableExecutable;

namespace DotNetOutdated.Tests
{
    public class DependenciesFileTests
    {
        private const string _packageName = "Mediatr";
        private readonly Context _context = new();

        [Fact]
        public void ShouldNotUpgradeProjectFile()
        {
            var subject = new DependencyFileAddPackageService(_context.FileSystem, _context.DependenciesFilePath);
            RunStatus status = subject.AddPackage(_context.ProjectPath, _packageName, null, new NuGetVersion(12, 4, 0), false);

            Assert.NotNull(status);
            Assert.Equal(0, status.ExitCode);
            Assert.Equal(_context.ProjectFileContent, _context.GetProjectFileContent());
        }

        [Fact]
        public void ShouldUpgradeDependenciesFile()
        {
            var subject = new DependencyFileAddPackageService(_context.FileSystem, _context.DependenciesFilePath);
            RunStatus status = subject.AddPackage(_context.ProjectPath, _packageName, null, new NuGetVersion(12, 4, 0), false);

            Assert.NotNull(status);
            Assert.Equal(0, status.ExitCode);
            Assert.NotEqual(_context.DependenciesFileContent, _context.GetDependenciesContent());
        }

        [Fact]
        public void ShouldUpgradeVersionAttribute()
        {
            var subject = new DependencyFileAddPackageService(_context.FileSystem, _context.DependenciesFilePath);
            var version = new NuGetVersion(12, 4, 0);
            var initialVersion = _context.GetPackageVersion(_packageName);
            _ = subject.AddPackage(_context.ProjectPath, _packageName, null, version, false);
            var updatedVersion = _context.GetPackageVersion(_packageName);

            Assert.NotEqual(version, initialVersion);
            Assert.Equal(version, updatedVersion);
        }

        [Fact]
        public void ShouldUpgradeVariable()
        {
            var subject = new DependencyFileAddPackageService(_context.FileSystem, _context.DependenciesFilePath);
            var version = new NuGetVersion(12, 4, 0);
            var packageName = "OpenTelemetry";
            var variableName = "OpenTelemetryVersion";
            var initialVersion = _context.GetVariableVersion(variableName);
            _ = subject.AddPackage(_context.ProjectPath, packageName, null, version, false);
            var updatedVersion = _context.GetVariableVersion(variableName);

            Assert.NotEqual(version, initialVersion);
            Assert.Equal(version, updatedVersion);
        }

        [Fact]
        public void ShouldNotUpgradeVariableContainingVariable()
        {
            var subject = new DependencyFileAddPackageService(_context.FileSystem, _context.DependenciesFilePath);
            var packageName = "OpenTelemetry.Instrumentation.Http";
            var variableName = "OpenTelemetryInstrumentationVersion";
            var initialValue = _context.GetVariableValue(variableName);
            var initialVersion = _context.GetPackageVersionString(packageName);
            _ = subject.AddPackage(_context.ProjectPath, packageName, null, new NuGetVersion(12, 4, 0), false);
            var updatedValue = _context.GetVariableValue(variableName);
            var updatedVersion = _context.GetPackageVersionString(packageName);

            Assert.Equal(initialValue, updatedValue);
            Assert.Equal(initialVersion, updatedVersion);
        }

        private sealed class Context
        {
            public Context()
            {
                MockRestoreService = Substitute.For<IDotNetRestoreService>();
                MockRestoreService.Restore(Arg.Any<string>()).Returns(new RunStatus(string.Empty, string.Empty, 0));
                FileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());
                ProjectPath = MockUnixSupport.Path(@"c:\source\project\app\app.csproj");
                FileSystem.AddFileFromEmbeddedResource(ProjectPath, GetType().Assembly, "DotNetOutdated.Tests.TestData.DependenciesTest.csproj");
                ProjectFileContent = FileSystem.GetFile(ProjectPath).TextContents;

                DependenciesFilePath = MockUnixSupport.Path(@"c:\source\project\msbuild\dependencies.props");
                FileSystem.AddFileFromEmbeddedResource(DependenciesFilePath, GetType().Assembly, "DotNetOutdated.Tests.TestData.dependencies.props");
                DependenciesFileContent = FileSystem.GetFile(DependenciesFilePath).TextContents;
            }

            public IDotNetRestoreService MockRestoreService { get; }
            public MockFileSystem FileSystem { get; }
            public string ProjectPath { get; }
            public string ProjectFileContent { get; }
            public string DependenciesFilePath { get; }
            public object DependenciesFileContent { get; }

            public string GetDependenciesContent()
            {
                return FileSystem.GetFile(DependenciesFilePath).TextContents;
            }

            public string GetProjectFileContent()
            {
                return FileSystem.GetFile(ProjectPath).TextContents;
            }

            internal NuGetVersion GetPackageVersion(string packageName)
            {
                var version = GetPackageVersionString(packageName);
                return !string.IsNullOrWhiteSpace(version)
                    ? NuGetVersion.Parse(version)
                    : throw new ArgumentException($"{packageName} not found", nameof(packageName));
            }

            internal string GetPackageVersionString(string packageName)
            {
                using var stream = FileSystem.FileInfo.New(DependenciesFilePath).OpenRead();
                var doc = XDocument.Load(stream);
                var match = doc.Root.XPathSelectElement($"//PackageReference[@Update='{packageName}']");
                return match != null
                    ? match.Attribute("Version").Value
                    : throw new ArgumentException($"{packageName} not found", nameof(packageName));
            }

            internal NuGetVersion GetVariableVersion(string variableName)
            {
                var value = GetVariableValue(variableName);
                return !string.IsNullOrWhiteSpace(value)
                    ? NuGetVersion.Parse(value)
                    : throw new ArgumentException($"{variableName} not found", nameof(variableName));
            }

            internal string GetVariableValue(string variableName)
            {
                using var stream = FileSystem.FileInfo.New(DependenciesFilePath).OpenRead();
                var doc = XDocument.Load(stream);
                var match = doc.Root.XPathSelectElement($"//PropertyGroup/{variableName}");
                return match != null
                    ? match.Value
                    : throw new ArgumentException($"{variableName} not found", nameof(variableName));
            }
        }
    }
}
