using DotNetOutdated.Core;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class ProjectPathExtensionsTests
    {
        [Theory]
        [InlineData("/repo/App.csproj", true)]
        [InlineData("/repo/App.fsproj", true)]
        [InlineData("/repo/App.CSPROJ", true)]
        [InlineData("/repo/App.FsProj", true)]
        [InlineData("/repo/App.vbproj", false)]
        [InlineData("/repo/App.sln", false)]
        [InlineData("/repo/app.cs", false)]
        [InlineData("/repo/App.csproj.user", false)]
        [InlineData("/repo/csproj", false)]
        public void IsProjectFile_MatchesCSharpAndFSharpProjectsIgnoringCase(string path, bool expected)
        {
            Assert.Equal(expected, path.IsProjectFile());
        }
    }
}
