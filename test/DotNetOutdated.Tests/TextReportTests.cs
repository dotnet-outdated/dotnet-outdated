namespace DotNetOutdated.Tests
{
    //    public class TextReportTests
    //    {
    //        private readonly ITestOutputHelper _output;
    //
    //        public TextReportTests(ITestOutputHelper output)
    //        {
    //            _output = output;
    //        }
    //
    //        [Theory]
    //        [InlineData("DotNetOutdated",
    //            ".NETCoreApp",
    //            "2.1",
    //            "NuGet.Versioning",
    //            "4.8.0-preview1.5156",
    //            "4.8.0-preview1.5156",
    //            "DotNetOutdated;.NETCoreApp,Version=v2.1;NuGet.Versioning;4.8.0-preview1.5156;4.8.0-preview1.5156;Major")]
    //        [InlineData("Some.Awesome.Project",
    //            ".NETStandard",
    //            "2.0",
    //            "Some.Awesome.Package",
    //            "1.2.0",
    //            "1.2.0",
    //            "Some.Awesome.Project;.NETStandard,Version=v2.0;Some.Awesome.Package;1.2.0;1.2.0;None")]
    //        [InlineData("Some.Awesome.Project",
    //            ".NETStandard",
    //            "2.0",
    //            "Some.Awesome.Package",
    //            "1.2.0",
    //            "1.2.1",
    //            "Some.Awesome.Project;.NETStandard,Version=v2.0;Some.Awesome.Package;1.2.0;1.2.1;Patch")]
    //        [InlineData("Some.Awesome.Project",
    //            ".NETStandard",
    //            "2.0",
    //            "Some.Awesome.Package",
    //            "1.2.0",
    //            "1.3.1",
    //            "Some.Awesome.Project;.NETStandard,Version=v2.0;Some.Awesome.Package;1.2.0;1.3.1;Minor")]
    //        [InlineData("Some.Awesome.Project",
    //            ".NETStandard",
    //            "2.0",
    //            "Some.Awesome.Package",
    //            "1.2.0",
    //            "2.0.0",
    //            "Some.Awesome.Project;.NETStandard,Version=v2.0;Some.Awesome.Package;1.2.0;2.0.0;Major")]
    //        public void TextReportLine(string projectName, string targetFrameworkName, string targetFrameworkVersion, string dependencyName, string resolved, string latest, string expectedResult)
    //        {
    //            var project = new AnalyzedProject
    //            {
    //                Name = projectName
    //            };
    //            var frameworkVersion = new Version(targetFrameworkVersion);
    //            var targetFramework = new AnalyzedTargetFramework
    //            {
    //                Name = new NuGetFramework(targetFrameworkName, frameworkVersion)
    //            };
    //            var dependency = new AnalyzedDependency
    //            {
    //                Name = dependencyName,
    //                ResolvedVersion = new NuGetVersion(resolved),
    //            };
    //            if (latest != null)
    //            {
    //                dependency.LatestVersion = new NuGetVersion(latest);
    //            }
    //
    //            var actualResult = Report.GetTextReportLine(project, targetFramework, dependency);
    //
    //            Assert.Equal(expectedResult, actualResult);
    //        }
    //
    //        [Theory]
    //        [InlineData("Some.Awesome.Project",
    //            ".NETStandard",
    //            "2.0",
    //            "Some.Awesome.Package",
    //            "1.2.0",
    //            "Some.Awesome.Project;.NETStandard,Version=v2.0;Some.Awesome.Package;1.2.0;;Unknown")]
    //        public void TextReportLineWithoutLatestVersion(string projectName, string targetFrameworkName, string targetFrameworkVersion, string dependencyName, string resolved, string expectedResult)
    //        {
    //            var project = new AnalyzedProject
    //            {
    //                Name = projectName
    //            };
    //            var frameworkVersion = new Version(targetFrameworkVersion);
    //            var targetFramework = new AnalyzedTargetFramework
    //            {
    //                Name = new NuGetFramework(targetFrameworkName, frameworkVersion)
    //            };
    //            var dependency = new AnalyzedDependency
    //            {
    //                Name = dependencyName,
    //                ResolvedVersion = new NuGetVersion(resolved),
    //            };
    //
    //            var actualResult = Report.GetTextReportLine(project, targetFramework, dependency);
    //
    //            Assert.Equal(expectedResult, actualResult);
    //        }
    //    }
}
