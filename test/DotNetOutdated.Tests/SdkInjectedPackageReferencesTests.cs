using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class SdkInjectedPackageReferencesTests
    {
        private static readonly string PackagesFolder = XFS.Path(@"c:\nuget\packages");
        private static readonly string FallbackFolder = XFS.Path(@"c:\nuget\fallback");
        private static readonly string ProjectFile = XFS.Path(@"c:\repo\app\app.csproj");
        private static readonly string SdkTargets = XFS.Path(@"c:\nuget\packages\cake.sdk\6.0.0\Sdk\Sdk.targets");

        [Theory]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />", false)]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Web\" />", false)]
        [InlineData("<Project Sdk=\"microsoft.net.sdk.razor\" />", false)]
        [InlineData("<Project Sdk=\"MSTest.Sdk/3.6.0\" />", true)]
        [InlineData("<Project Sdk=\" Microsoft.NET.Sdk ; Cake.Sdk/6.0.0 \" />", true)]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><Sdk Name=\"Aspire.AppHost.Sdk\" Version=\"9.0.0\" /></Project>", true)]
        [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" /></Project>", false)]
        [InlineData("<Project><Import Project=\"Sdk.targets\" Sdk=\"Uno.Sdk\" /></Project>", true)]
        [InlineData("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" Sdk=\"Cake.Sdk/6.0.0\" />", true)]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Sdk>Cake.Sdk</Sdk></PropertyGroup></Project>", false)]
        [InlineData("<Project><PropertyGroup /></Project>", false)]
        [InlineData("<Project Sdk=\"Cake.Sdk\"", false)]
        [InlineData("<Project Sdk=\"Microsoft.NET.SdkExtras/1.0.0\" />", true)]
        public void DeclaresCustomSdk(string xml, bool expected)
        {
            Assert.Equal(expected, SdkInjectedPackageReferences.DeclaresCustomSdk(xml));
        }

        [Theory]
        [InlineData(@"c:\repo\app\Directory.Build.props")]
        [InlineData(@"c:\repo\Directory.Build.targets")]
        [InlineData(@"c:\Directory.Build.props")]
        public void CustomSdkImportedFromDirectoryBuildFileIsDetected(string directoryBuildFile)
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [ProjectFile] = new("<Project Sdk=\"Microsoft.NET.Sdk\" />"),
                [XFS.Path(directoryBuildFile)] = new("<Project><Import Project=\"Sdk.targets\" Sdk=\"Cake.Sdk\" /></Project>"),
            });

            Assert.True(SdkInjectedPackageReferences.UsesCustomSdk(fileSystem, ProjectFile));
        }

        [Fact]
        public void CustomSdkInProjectFileIsDetected()
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [ProjectFile] = new("<Project Sdk=\"Cake.Sdk/6.0.0\" />"),
            });

            Assert.True(SdkInjectedPackageReferences.UsesCustomSdk(fileSystem, ProjectFile));
        }

        [Fact]
        public void ProjectWithoutCustomSdkIsNotDetected()
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [ProjectFile] = new("<Project Sdk=\"Microsoft.NET.Sdk\" />"),
                [XFS.Path(@"c:\repo\Directory.Build.props")] = new("<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>"),
                [XFS.Path(@"c:\repo\other\Directory.Build.props")] = new("<Project><Import Project=\"Sdk.props\" Sdk=\"Cake.Sdk\" /></Project>"),
                [XFS.Path(@"c:\repo\Directory.Build.targets")] = new("not xml"),
            });

            Assert.False(SdkInjectedPackageReferences.UsesCustomSdk(fileSystem, ProjectFile));
        }

        [Fact]
        public void MissingProjectFileIsNotDetected()
        {
            Assert.False(SdkInjectedPackageReferences.UsesCustomSdk(new MockFileSystem(), ProjectFile));
        }

        [Fact]
        public void PackageDefinedInPackagesFolderIsReturned()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Cake.Generator", SdkTargets));

            Assert.Equal(["Cake.Generator"], names);
        }

        [Fact]
        public void PackageDefinedInFallbackFolderIsReturned()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Cake.Generator", XFS.Path(@"c:\nuget\fallback\cake.sdk\6.0.0\Sdk\Sdk.targets")));

            Assert.Equal(["Cake.Generator"], names);
        }

        [Fact]
        public void PackageDefinedInTheProjectIsNotReturned()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Newtonsoft.Json", ProjectFile));

            Assert.Empty(names);
        }

        [Fact]
        public void PackageAlsoReferencedByTheUserIsNotReturned()
        {
            var names = GetPackageDefinedNames(
                new PackageReferenceItem("Cake.Generator", SdkTargets),
                new PackageReferenceItem("cake.generator", XFS.Path(@"c:\repo\Directory.Build.props")));

            Assert.Empty(names);
        }

        [Fact]
        public void FolderWithTheSamePrefixAsThePackagesFolderIsNotAPackageFolder()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Cake.Generator", XFS.Path(@"c:\nuget\packages2\cake.sdk\Sdk.targets")));

            Assert.Empty(names);
        }

        [Fact]
        public void PackagesFolderMatchesPathsWithThePlatformCaseSensitivity()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Cake.Generator", XFS.Path(@"c:\nuget\Packages\Directory.Build.props")));

            // On Linux, /nuget/Packages is a different directory from /nuget/packages.
            Assert.Equal(!OperatingSystem.IsLinux(), names.Contains("Cake.Generator"));
        }

        [Fact]
        public void ItemWithoutDefiningFileIsNotReturned()
        {
            var names = GetPackageDefinedNames(new PackageReferenceItem("Cake.Generator", null));

            Assert.Empty(names);
        }

        [Fact]
        public void EmptyAndMissingPackageFoldersAreIgnored()
        {
            var names = SdkInjectedPackageReferences.GetPackageDefinedNames(
                new MockFileSystem(),
                [new PackageReferenceItem("Cake.Generator", SdkTargets)],
                [null, string.Empty]);

            Assert.Empty(names);
        }

        [Fact]
        public void MarksOnlyNamedDirectDependencies()
        {
            var targetFramework = new TargetFramework(NuGetFramework.Parse("net8.0"));
            targetFramework.Dependencies["Cake.Generator"] = CreateDependency("Cake.Generator", isTransitive: false, isDevelopmentDependency: true);
            targetFramework.Dependencies["Newtonsoft.Json"] = CreateDependency("Newtonsoft.Json", isTransitive: false);
            targetFramework.Dependencies["Microsoft.CodeCoverage"] = CreateDependency("Microsoft.CodeCoverage", isTransitive: true);

            SdkInjectedPackageReferences.MarkAsAutoReferenced(
                targetFramework,
                new HashSet<string>(["cake.generator", "Microsoft.CodeCoverage"], StringComparer.OrdinalIgnoreCase));

            var cakeGenerator = targetFramework.Dependencies["Cake.Generator"];
            Assert.True(cakeGenerator.IsAutoReferenced);
            Assert.False(cakeGenerator.IsTransitive);
            Assert.True(cakeGenerator.IsDevelopmentDependency);
            Assert.Equal(NuGetVersion.Parse("6.0.0"), cakeGenerator.ResolvedVersion);
            Assert.False(targetFramework.Dependencies["Newtonsoft.Json"].IsAutoReferenced);
            Assert.False(targetFramework.Dependencies["Microsoft.CodeCoverage"].IsAutoReferenced);
        }

        private static HashSet<string> GetPackageDefinedNames(params PackageReferenceItem[] items) =>
            SdkInjectedPackageReferences.GetPackageDefinedNames(new MockFileSystem(), items, [PackagesFolder + System.IO.Path.DirectorySeparatorChar, FallbackFolder]);

        private static Dependency CreateDependency(string name, bool isTransitive, bool isDevelopmentDependency = false) =>
            new(name, VersionRange.Parse("6.0.0"), NuGetVersion.Parse("6.0.0"), isAutoReferenced: false, isTransitive, isDevelopmentDependency);
    }
}
