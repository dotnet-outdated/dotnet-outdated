using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DotNetOutdated.Tests;

public class GitHubRepositoryStatusServiceTests
{
    [Theory]
    [InlineData("https://github.com/dotnet-outdated/dotnet-outdated", "dotnet-outdated", "dotnet-outdated")]
    [InlineData("https://www.github.com/dotnet-outdated/dotnet-outdated.git", "dotnet-outdated", "dotnet-outdated")]
    [InlineData("git@github.com:dotnet-outdated/dotnet-outdated.git", "dotnet-outdated", "dotnet-outdated")]
    public void TryGetGitHubRepositoryParsesOwnerAndRepository(string url, string expectedOwner, string expectedRepository)
    {
        bool parsed = GitHubRepositoryStatusService.TryGetGitHubRepository(url, out string owner, out string repository);

        Assert.True(parsed);
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedRepository, repository);
    }

    [Theory]
    [InlineData("https://gitlab.com/dotnet-outdated/dotnet-outdated")]
    [InlineData("https://github.com/dotnet-outdated")]
    [InlineData("")]
    public void TryGetGitHubRepositoryRejectsUnsupportedUrl(string url)
    {
        Assert.False(GitHubRepositoryStatusService.TryGetGitHubRepository(url, out _, out _));
    }

    [Theory]
    [InlineData(false, RepositoryStatus.Active)]
    [InlineData(true, RepositoryStatus.Archived)]
    public async Task GetRepositoryStatusHandlesSuccessfulResponse(bool archived, RepositoryStatus expectedStatus)
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/owner/repository", request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"archived\":{archived.ToString().ToLowerInvariant()}}}")
            });
        });
        var service = CreateService("https://github.com/owner/repository", handler);

        RepositoryStatus status = await GetRepositoryStatus(service);

        Assert.Equal(expectedStatus, status);
    }

    [Fact]
    public async Task GetRepositoryStatusHandlesNotFoundResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = CreateService("https://github.com/owner/repository", handler);

        RepositoryStatus status = await GetRepositoryStatus(service);

        Assert.Equal(RepositoryStatus.NotFound, status);
    }

    [Fact]
    public async Task GetRepositoryStatusHandlesNetworkFailure()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException());
        var service = CreateService("https://github.com/owner/repository", handler);

        RepositoryStatus status = await GetRepositoryStatus(service);

        Assert.Equal(RepositoryStatus.Unknown, status);
    }

    [Fact]
    public async Task GetRepositoryStatusHandlesTimeout()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException());
        var service = CreateService("https://github.com/owner/repository", handler);

        RepositoryStatus status = await GetRepositoryStatus(service);

        Assert.Equal(RepositoryStatus.Unknown, status);
    }

    [Fact]
    public async Task GetRepositoryStatusSkipsNonGitHubRepository()
    {
        int requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = CreateService("https://gitlab.com/owner/repository", handler);

        RepositoryStatus status = await GetRepositoryStatus(service);

        Assert.Equal(RepositoryStatus.Unknown, status);
        Assert.Equal(0, requestCount);
    }

    private static GitHubRepositoryStatusService CreateService(string repositoryUrl, HttpMessageHandler handler)
    {
        var packageInfoService = Substitute.For<INuGetPackageInfoService>();
        packageInfoService.GetRepositoryUrl(
                Arg.Any<string>(),
                Arg.Any<NuGetVersion>(),
                Arg.Any<IEnumerable<Uri>>(),
                Arg.Any<string>())
            .Returns(repositoryUrl);

        return new GitHubRepositoryStatusService(packageInfoService, new HttpClient(handler));
    }

    private static Task<RepositoryStatus> GetRepositoryStatus(GitHubRepositoryStatusService service)
    {
        return service.GetRepositoryStatus(
            "Package",
            NuGetVersion.Parse("1.0.0"),
            Array.Empty<Uri>(),
            "project.csproj");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
