using DotNetOutdated.Core.Services;
using Moq;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class CentralPackageVersionManagementTests
    {
        [Fact]
        public void UpgradingCPVMEnabledPackageUpdatesNearestCPVMFile()
        {
            SetupCPVMMocks(out Mock<IDotNetRestoreService> mockRestoreService, out MockFileSystem mockFileSystem, out string path, out string nearestCPVMFilePath, out string rootCPVMFilePath, out string rootCPVMFileContent, out string _, out string _);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService.Object);
            RunStatus status = subject.AddPackage(path, "FakePackage", new NuGet.Versioning.NuGetVersion(2, 0, 0), false);

            Assert.NotNull(status);
            Assert.Equal(0, status.ExitCode);

            Assert.Equal(rootCPVMFileContent, mockFileSystem.GetFile(rootCPVMFilePath).TextContents);
            Assert.NotEqual(rootCPVMFileContent, mockFileSystem.GetFile(nearestCPVMFilePath).TextContents);
        }

        [Fact]
        public void UpgradingCPVMEnabledPackageDoesNotModifyProjectFile()
        {
            SetupCPVMMocks(out Mock<IDotNetRestoreService> mockRestoreService, out MockFileSystem mockFileSystem, out string path, out string _, out string _, out string _, out string _, out string projectFileContent);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService.Object);
            RunStatus status = subject.AddPackage(path, "FakePackage", new NuGet.Versioning.NuGetVersion(2, 0, 0), false);

            Assert.Equal(0, status.ExitCode);
            Assert.Equal(projectFileContent, mockFileSystem.GetFile(path).TextContents);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UpgradingCPVMEnabledPackageRespectsNoRestoreFlag(bool noRestore)
        {
            SetupCommonMocks(out Mock<IDotNetRestoreService> mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string _);

            var subject = new CentralPackageVersionManagementService(mockFileSystem, mockRestoreService.Object);
            RunStatus status = subject.AddPackage(projectPath, "FakePackage", new NuGet.Versioning.NuGetVersion(1, 0, 0), noRestore);

            if (noRestore)
            {
                Assert.Empty(mockRestoreService.Invocations);
            }
            else
            {
                mockRestoreService.Verify(x => x.Restore(projectPath));
            }
        }

        private void SetupCPVMMocks(out Mock<IDotNetRestoreService> mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string nearestCPVMFilePath, out string rootCPVMFilePath, out string rootCPVMFileContent, out string nearestCPVMFileContent, out string projectFileContent)
        {
            SetupCommonMocks(out mockRestoreService, out mockFileSystem, out projectPath, out projectFileContent);

            nearestCPVMFilePath = MockUnixSupport.Path(@"c:\source\project\Directory.Packages.props");
            mockFileSystem.AddFileFromEmbeddedResource(nearestCPVMFilePath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMFile.props");
            nearestCPVMFileContent = mockFileSystem.GetFile(nearestCPVMFilePath).TextContents;

            rootCPVMFilePath = MockUnixSupport.Path(@"c:\source\Directory.Packages.props");
            mockFileSystem.AddFileFromEmbeddedResource(rootCPVMFilePath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMFile.props");
            rootCPVMFileContent = mockFileSystem.GetFile(rootCPVMFilePath).TextContents;
        }

        private void SetupCommonMocks(out Mock<IDotNetRestoreService> mockRestoreService, out MockFileSystem mockFileSystem, out string projectPath, out string projectFileContent)
        {
            mockRestoreService = new Mock<IDotNetRestoreService>();
            mockRestoreService.Setup(x => x.Restore(It.IsAny<string>())).Returns(new RunStatus(string.Empty, string.Empty, 0));

            mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>());

            projectPath = MockUnixSupport.Path(@"c:\source\project\app\app.csproj");

            mockFileSystem.AddFileFromEmbeddedResource(projectPath, GetType().Assembly, "DotNetOutdated.Tests.TestData.CPVMProject.csproj");
            projectFileContent = mockFileSystem.GetFile(projectPath).TextContents;
        }
    }
}
