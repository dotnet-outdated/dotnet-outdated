#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NuGet.Frameworks;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class NuGetPackageInfoServiceTests
    {
        [Fact]
        public async Task GetAllVersionsAsync_StopsWaitingForAStalledFeedWhenCancelled()
        {
            // A feed that accepts connections but never answers, so service index discovery doesn't complete.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var service = new NuGetPackageInfoService();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            var lookup = service.GetAllVersionsAsync(
                new PackageVersionsQuery
                {
                    PackageId = "Contoso.Widgets",
                    Sources = [new Uri($"http://127.0.0.1:{port}/v3/index.json")],
                    TargetFramework = NuGetFramework.Parse("net9.0"),
                },
                cancellation.Token);

            var finished = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(lookup, finished);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        }
    }
}
