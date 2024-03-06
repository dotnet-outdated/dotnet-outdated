using DotNetOutdated.Core.Services;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using NSubstitute;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class CentralPackageVersionManagementTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UpgradingCPVMEnabledPackageUpdatesNearestCPVMFile(bool isGlobalPackage)
        {
            SetupCPVMMocks(out IDotNetRestoreService mockRestoreService, out MockFileSystem mockFileSystem, out string path, out string nearestCPVMFilePath, out string rootCPVMFilePath, out string rootCPVMFileContent, out string _, out string _);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService);
            RunStatus status = subject.AddPackage(path, isGlobalPackage ? "GlobalFakePackage" : "FakePackage", new NuGet.Versioning.NuGetVersion(2, 0, 0), false);

            Assert.NotNull(status);
            Assert.Equal(0, status.ExitCode);

            Assert.Equal(rootCPVMFileContent, mockFileSystem.GetFile(rootCPVMFilePath).TextContents);
            Assert.NotEqual(rootCPVMFileContent, mockFileSystem.GetFile(nearestCPVMFilePath).TextContents);
        }

        [Fact]
        public void UpgradingCPVMEnabledPackageDoesNotModifyProjectFile()
        {
            SetupCPVMMocks(out IDotNetRestoreService mockRestoreService, out MockFileSystem mockFileSystem, out string path, out string _, out string _, out string _, out string _, out string projectFileContent);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService);
            RunStatus status = subject.AddPackage(path, "FakePackage", new NuGet.Versioning.NuGetVersion(2, 0, 0), false);

            Assert.Equal(0, status.ExitCode);
            Assert.Equal(projectFileContent, mockFileSystem.GetFile(path).TextContents);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UpgradingCPVMEnabledPackageRespectsNoRestoreFlag(bool noRestore)
        {
            SetupCommonMocks(out IDotNetRestoreService mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string _);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService);
            RunStatus status = subject.AddPackage(projectPath, "FakePackage", new NuGet.Versioning.NuGetVersion(1, 0, 0), noRestore);

            if (noRestore)
            {
                mockRestoreService.DidNotReceiveWithAnyArgs().Restore(default);
            }
            else
            {
                mockRestoreService.Received().Restore(projectPath);
            }
        }

        private void SetupCPVMMocks(out IDotNetRestoreService mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string nearestCPVMFilePath, out string rootCPVMFilePath, out string rootCPVMFileContent, out string nearestCPVMFileContent, out string projectFileContent)
        {
            SetupCommonMocks(out mockRestoreService, out mockFileSystem, out projectPath, out projectFileContent);

            nearestCPVMFilePath = MockUnixSupport.Path(@"c:\source\project\Directory.Packages.props");
            mockFileSystem.AddFileFromEmbeddedResource(nearestCPVMFilePath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMFile.props");
            nearestCPVMFileContent = mockFileSystem.GetFile(nearestCPVMFilePath).TextContents;

            rootCPVMFilePath = MockUnixSupport.Path(@"c:\source\Directory.Packages.props");
            mockFileSystem.AddFileFromEmbeddedResource(rootCPVMFilePath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMFile.props");
            rootCPVMFileContent = mockFileSystem.GetFile(rootCPVMFilePath).TextContents;
        }

        private void SetupCommonMocks(out IDotNetRestoreService mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string projectFileContent)
        {
            mockRestoreService = Substitute.For<IDotNetRestoreService>();
            mockRestoreService.Restore(Arg.Any<string>()).Returns(new RunStatus(string.Empty, string.Empty, 0));

            mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            projectPath = MockUnixSupport.Path(@"c:\source\project\app\app.csproj");

            mockFileSystem.AddFileFromEmbeddedResource(projectPath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMProject.csproj");
            projectFileContent = mockFileSystem.GetFile(projectPath).TextContents;
        }
    }
}
