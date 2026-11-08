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
        // Run a process that stays alive and silent for far longer than the idle timeout, so the runner
        // always reaches the timeout branch rather than racing a process that might exit first.
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
            : ("sleep", new[] { "60" });
        var runner = new DotNetRunner(new DotNetRunnerOptions { IdleTimeout = TimeSpan.FromSeconds(1), FileName = fileName });

        RunStatus status = runner.Run(Path.GetTempPath(), arguments);

        Assert.False(status.IsSuccess);
        Assert.Equal(-1, status.ExitCode);
        Assert.Contains($"'{fileName} {string.Join(' ', arguments)}' produced no output for 1 seconds", status.Errors);
        Assert.Contains("--idle-timeout", status.Errors);
    }
}
