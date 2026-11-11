#nullable enable

using System;
using System.IO.Abstractions.TestingHelpers;
using System.Threading;
using System.Threading.Tasks;
using DotNetOutdated.Core.Services;
using NSubstitute;
using NuGet.Common;
using Xunit;

namespace DotNetOutdated.Tests
{
    public class ProjectAnalysisServiceTests
    {
        [Fact]
        public async Task ACancelledRequest_DoesNotEvaluateTheProject()
        {
            var evaluator = Substitute.For<IProjectEvaluator>();
            var service = new ProjectAnalysisService(evaluator, Substitute.For<IDotNetRestoreService>(), new MockFileSystem(), NullLogger.Instance);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.AnalyzeProjectAsync("/repo/App.csproj", cancellationToken: cancellation.Token));

            await evaluator.DidNotReceiveWithAnyArgs().GetRestoreGraphAsync(default!, default);
        }
    }
}
