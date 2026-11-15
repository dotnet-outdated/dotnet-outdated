#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Frameworks;
using NuGet.Protocol;
using Xunit;

namespace DotNetOutdated.Tests;

// NuGet keeps one credential service for the whole process, so these tests run on their own.
[CollectionDefinition(Name, DisableParallelization = true)]
public class NuGetCredentialServiceTestsCollection
{
    public const string Name = nameof(NuGetCredentialServiceTestsCollection);
}

/// <summary>
/// Looking up packages sets up NuGet's credential service (credential providers for authenticated feeds) for every
/// host, not only the command line, and leaves one the host set up itself in place.
/// </summary>
[Collection(NuGetCredentialServiceTestsCollection.Name)]
public sealed class NuGetCredentialServiceTests : IDisposable
{
    private readonly Lazy<ICredentialService>? _original = HttpHandlerResourceV3.CredentialService;

    public void Dispose() => HttpHandlerResourceV3.CredentialService = _original;

    [Fact]
    public async Task LookingUpVersions_SetsUpTheCredentialService()
    {
        HttpHandlerResourceV3.CredentialService = null;
        using var service = new NuGetPackageInfoService(NullLogger.Instance);

        await LookUpVersions(service);

        Assert.NotNull(HttpHandlerResourceV3.CredentialService);
    }

    [Fact]
    public async Task LookingUpVersions_KeepsACredentialServiceTheHostSetUp()
    {
        var hostService = new Lazy<ICredentialService>(() => Substitute.For<ICredentialService>());
        HttpHandlerResourceV3.CredentialService = hostService;
        using var service = new NuGetPackageInfoService(NullLogger.Instance);

        await LookUpVersions(service);

        Assert.Same(hostService, HttpHandlerResourceV3.CredentialService);
    }

    private static Task LookUpVersions(NuGetPackageInfoService service) =>
        service.GetAllVersionsAsync(new PackageVersionsQuery
        {
            PackageId = "Contoso.Widgets",
            Sources = [new Uri(Path.Combine(Path.GetTempPath(), "dotnet-outdated-no-feed-" + Guid.NewGuid().ToString("N")))],
            TargetFramework = NuGetFramework.Parse("net9.0"),
        });
}
