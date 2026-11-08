using System;
using System.Collections.Generic;
using DotNetOutdated.Core;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;

namespace DotNetOutdated.Tests
{
    // Implementations written against the interfaces before the VersionRange overloads existed must keep compiling and working.
    public class RangeOverloadDefaultImplementationTests
    {
        private sealed class LegacyPackageService : IDotNetPackageService
        {
            public NuGetVersion LastVersion { get; private set; }

            public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version) =>
                AddPackage(projectPath, packageName, frameworkName, version, false, false);

            public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources)
            {
                LastVersion = version;
                return new RunStatus(string.Empty, string.Empty, 0);
            }

            public RunStatus RemovePackage(string projectPath, string packageName) => new(string.Empty, string.Empty, 0);
        }

        private sealed class LegacyVariableTrackingService : IVariableTrackingService
        {
            public NuGetVersion LastVersion { get; private set; }

            public Dictionary<string, PackageVariableInfo> DiscoverPackageVariables(string projectFilePath) => [];

            public void UpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion) => TryUpdatePackageVariable(variableInfo, newVersion);

            public bool TryUpdatePackageVariable(PackageVariableInfo variableInfo, NuGetVersion newVersion)
            {
                LastVersion = newVersion;
                return true;
            }

            public bool UpdateFileBasedAppDirectReference(string projectFilePath, string name, FileBasedAppReferenceKind kind, NuGetVersion newVersion) => false;

            public IReadOnlyList<FileBasedAppReference> DiscoverFileBasedAppReferences(string projectFilePath) => [];

            public void ClearCache()
            {
            }
        }

        [Fact]
        public void PackageService_PlainVersionRange_DelegatesToNuGetVersionOverload()
        {
            var legacy = new LegacyPackageService();
            IDotNetPackageService service = legacy;

            var result = service.AddPackage("p.csproj", "Newtonsoft.Json", "net8.0", new VersionRange(new NuGetVersion("13.0.4")));

            Assert.True(result.IsSuccess);
            Assert.Equal(new NuGetVersion("13.0.4"), legacy.LastVersion);
        }

        [Fact]
        public void PackageService_BoundedRange_IsNotSupported()
        {
            IDotNetPackageService service = new LegacyPackageService();

            Assert.Throws<NotSupportedException>(() =>
                service.AddPackage("p.csproj", "Newtonsoft.Json", "net8.0", VersionRange.Parse("[13.0.4, 14.0.0)")));
        }

        [Fact]
        public void VariableTracking_PlainVersionRange_DelegatesToNuGetVersionOverload()
        {
            var legacy = new LegacyVariableTrackingService();
            IVariableTrackingService service = legacy;

            Assert.True(service.TryUpdatePackageVariable(new PackageVariableInfo(), new VersionRange(new NuGetVersion("13.0.4"))));
            Assert.Equal(new NuGetVersion("13.0.4"), legacy.LastVersion);
        }

        [Fact]
        public void VariableTracking_BoundedRange_IsNotUpdated()
        {
            var legacy = new LegacyVariableTrackingService();
            IVariableTrackingService service = legacy;

            Assert.False(service.TryUpdatePackageVariable(new PackageVariableInfo(), VersionRange.Parse("[13.0.4, 14.0.0)")));
            Assert.Null(legacy.LastVersion);
        }
    }
}
