using System;
using System.Diagnostics;
using System.Globalization;
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
        Assert.Equal(
            $"'{fileName} {string.Join(' ', arguments)}' produced no output for 1 seconds and was stopped because it " +
            "appeared to have hung. If the command is just slow (for example a long package restore), allow it more " +
            "time with --idle-timeout.",
            status.Errors);
    }

    [Fact]
    public void AProcessThatKeepsWriting_IsNotStoppedAfterTheIdleTimeout()
    {
        // Runs for well over the idle timeout, but never stays silent for as long as the timeout. The timeout leaves
        // room for a slow shell start (PowerShell on a loaded CI agent) before the first line.
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "1..8 | ForEach-Object { Write-Output $_; Start-Sleep -Milliseconds 750 }" })
            : ("sh", new[] { "-c", "for i in 1 2 3 4 5 6 7 8; do echo $i; sleep 0.75; done" });
        var runner = new DotNetRunner(new DotNetRunnerOptions { IdleTimeout = TimeSpan.FromSeconds(3), FileName = fileName });

        RunStatus status = runner.Run(Path.GetTempPath(), arguments);

        Assert.Equal(0, status.ExitCode);
        Assert.Equal(string.Join(Environment.NewLine, "1", "2", "3", "4", "5", "6", "7", "8") + Environment.NewLine, status.Output);
    }

    [Fact]
    public void AllOutputIsCollected_EvenWhenTheProcessExitsWhileItIsStillBeingRead()
    {
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "1..20000 | ForEach-Object { Write-Output $_ }; [Console]::Error.WriteLine('done'); exit 3" })
            : ("sh", new[] { "-c", "seq 1 20000; echo done >&2; exit 3" });
        var runner = new DotNetRunner(new DotNetRunnerOptions { FileName = fileName });

        RunStatus status = runner.Run(Path.GetTempPath(), arguments);

        Assert.Equal(3, status.ExitCode);
        var lines = status.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(20000, lines.Length);
        Assert.Equal("20000", lines[^1]);
        Assert.Equal("done" + Environment.NewLine, status.Errors);
    }

    [Fact]
    public void WhenTheIdleTimeoutExpires_TheProcessIsKilled()
    {
        // The process prints its id and then stays silent, so the runner times out on a process the test can look up.
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output $PID; Start-Sleep -Seconds 60" })
            : ("sh", new[] { "-c", "echo $$; exec sleep 60" });
        var runner = new DotNetRunner(new DotNetRunnerOptions { IdleTimeout = TimeSpan.FromSeconds(1), FileName = fileName });

        RunStatus status = runner.Run(Path.GetTempPath(), arguments);

        Assert.Equal(-1, status.ExitCode);
        int processId = int.Parse(status.Output.Trim(), CultureInfo.InvariantCulture);
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return; // Already gone.
        }

        using (process)
        {
            bool exited = process.WaitForExit(TimeSpan.FromSeconds(10));
            if (!exited)
            {
                process.Kill();
            }

            Assert.True(exited, "The hung process was left running.");
        }
    }
}
