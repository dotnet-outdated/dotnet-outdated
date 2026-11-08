using System;
using System.IO;
using DotNetOutdated.Core.Services;
using Xunit;

namespace DotNetOutdated.Tests;

public class DotNetRunnerTests
{
    [Fact]
    public void WhenTheIdleTimeoutExpires_TheErrorsSayTheProcessWasStopped()
    {
        // A zero idle timeout stops the process on the first check, before it can produce output.
        var runner = new DotNetRunner(new DotNetRunnerOptions { IdleTimeout = TimeSpan.Zero });

        RunStatus status = runner.Run(Path.GetTempPath(), ["--info"]);

        Assert.False(status.IsSuccess);
        Assert.Equal(-1, status.ExitCode);
        Assert.Contains("'dotnet --info' produced no output for 0 seconds", status.Errors);
        Assert.Contains("--idle-timeout", status.Errors);
    }
}
