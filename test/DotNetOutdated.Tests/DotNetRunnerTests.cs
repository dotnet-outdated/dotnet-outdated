using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using DotNetOutdated.Core.Services;
using Xunit;

namespace DotNetOutdated.Tests;

// These tests time real processes against the idle timeout. The runner resets that timer from stream readers on the
// thread pool, so they run on their own: with the rest of the suite running in parallel on a small CI agent, the pool
// can be busy for long enough that a process which never stops writing looks idle.
[CollectionDefinition(Name, DisableParallelization = true)]
public class DotNetRunnerTestsCollection
{
    public const string Name = "DotNetRunner timing";
}

[Collection(DotNetRunnerTestsCollection.Name)]
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
        // The timeout leaves room for a slow shell start (PowerShell on a loaded CI agent) before the id is printed.
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output $PID; Start-Sleep -Seconds 60" })
            : ("sh", new[] { "-c", "echo $$; exec sleep 60" });
        var runner = new DotNetRunner(new DotNetRunnerOptions { IdleTimeout = TimeSpan.FromSeconds(3), FileName = fileName });

        RunStatus status = runner.Run(Path.GetTempPath(), arguments);

        Assert.Equal(-1, status.ExitCode);
        if (!int.TryParse(status.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
        {
            // TEMPORARY DIAGNOSTIC: tells a starved thread pool apart from a slow or silent shell.
            ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            var pool = $"{ThreadPool.ThreadCount} threads, {maxWorkers - availableWorkers} busy workers, {ThreadPool.PendingWorkItemCount} pending items";
            var probe = Stopwatch.StartNew();
            using var direct = Process.Start(new ProcessStartInfo(fileName, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            // Read on a dedicated thread, off the thread pool, and give up after 30 seconds rather than hang the run.
            string firstLine = "(nothing)";
            TimeSpan probeTime = TimeSpan.Zero;
            var reader = new Thread(() =>
            {
                firstLine = direct.StandardOutput.ReadLine();
                probeTime = probe.Elapsed;
            }) { IsBackground = true };
            reader.Start();
            bool read = reader.Join(TimeSpan.FromSeconds(30));
            direct.Kill(entireProcessTree: true);
            Assert.Fail(
                $"No process id in the output. Output: '{status.Output}'. Errors: '{status.Errors}'. Thread pool: {pool}. " +
                (read
                    ? $"A second {fileName}, read on its own thread, printed '{firstLine}' after {probeTime.TotalMilliseconds:0} ms."
                    : $"A second {fileName}, read on its own thread, printed nothing in 30 seconds."));
        }
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
