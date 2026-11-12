using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class VersionLocatorTests
    {
        private readonly string _appPath = XFS.Path(@"c:\repo\app.cs");

        [Fact]
        public void FileBasedAppSdk_IsFoundInALiteralSdkDirective()
        {
            var location = Locate("#:sdk Cake.Sdk@6.0.0\n", "cake.sdk", ReferenceKind.Sdk);

            var directive = Assert.IsType<FileBasedDirectiveVersion>(location);
            Assert.Equal("Cake.Sdk", directive.Reference.Name);
            Assert.Equal(ReferenceKind.Sdk, directive.Reference.Kind);
            Assert.Equal(new NuGetVersion("6.0.0"), directive.Reference.ResolvedVersion);
        }

        [Theory]
        [InlineData("#:package Cake.Sdk@6.0.0\n")]
        [InlineData("#:package Cake.Sdk@6.0.0\n#:sdk Other.Sdk@1.0.0\n")]
        public void FileBasedAppSdk_IsNotFoundInAPackageDirectiveWithTheSameId(string content)
        {
            var location = Locate(content, "Cake.Sdk", ReferenceKind.Sdk);

            var notEditable = Assert.IsType<NotEditable>(location);
            Assert.Equal(
                $"Failed to update file-based app '{_appPath}' for 'Cake.Sdk'. Expected a literal #:sdk Cake.Sdk@<version> directive " +
                "(id match is case-insensitive; trailing comments are preserved).",
                notEditable.Message);
        }

        [Fact]
        public void FileBasedAppSdk_BehindAProperty_IsFoundAsAVariable()
        {
            var location = Locate("#:property CakeVersion=6.0.0\n#:sdk Cake.Sdk@$(CakeVersion)\n", "Cake.Sdk", ReferenceKind.Sdk);

            var variable = Assert.IsType<FileBasedVariableVersion>(location);
            Assert.Equal("CakeVersion", variable.Variable.VariableName);
        }

        private VersionLocation Locate(string content, string name, ReferenceKind? kind)
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [_appPath] = new(content),
            });
            var evaluator = new DotNetProjectEvaluator(Substitute.For<IDotNetRunner>(), fileSystem);
            var locator = new VersionLocator(fileSystem, evaluator, new FileBasedAppDirectives(fileSystem, evaluator));

            return locator.Locate(_appPath, name, kind);
        }
    }
}
