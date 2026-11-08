using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Runs dot net executable.
    /// </summary>
    /// <remarks>
    /// Credit for the stuff happening in here goes to the https://github.com/jaredcnance/dotnet-status project
    /// </remarks>
    public class DotNetRunner(DotNetRunnerOptions options) : IDotNetRunner
    {
        public DotNetRunner() : this(new DotNetRunnerOptions()) { }

        public RunStatus Run(string workingDirectory, string[] arguments)
        {
            var psi = new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var p = new Process();
            try
            {
                p.StartInfo = psi;
                p.Start();

                var output = new StringBuilder();
                var errors = new StringBuilder();
                var timeSinceLastOutput = Stopwatch.StartNew();
                var outputTask = ConsumeStreamReaderAsync(p.StandardOutput, timeSinceLastOutput, output);
                var errorTask = ConsumeStreamReaderAsync(p.StandardError, timeSinceLastOutput, errors);
                bool processExited = false;

                while (true) {
                    if (p.HasExited) {
                        processExited = true;
                        break;
                    }

                    // If output has not been received for a while, then
                    // assume that the process has hung and stop waiting.
                    lock(timeSinceLastOutput) {
                        if (timeSinceLastOutput.Elapsed > options.IdleTimeout) {
                            break;
                        }
                    }

                    Thread.Sleep(100);
                }

                if (!processExited)
                {
                    p.Kill();

                    // The stream readers may still be appending, so build the message separately rather than
                    // writing to the shared StringBuilder.
                    string timeoutMessage =
                        $"'dotnet {string.Join(' ', arguments)}' produced no output for {options.IdleTimeout.TotalSeconds:0} seconds " +
                        "and was stopped because it appeared to have hung. If the command is just slow (for example a long " +
                        "package restore), allow it more time with --idle-timeout.";

                    return new RunStatus(output.ToString(), errors.ToString() + timeoutMessage, exitCode: -1);
                }

                Task.WaitAll(outputTask, errorTask);

                return new RunStatus(output.ToString(), errors.ToString(), p.ExitCode);
            }
            finally
            {
                p.Dispose();
            }
        }

        private static async Task ConsumeStreamReaderAsync(StreamReader reader, Stopwatch timeSinceLastOutput, StringBuilder lines)
        {
            await Task.Yield();

            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (timeSinceLastOutput) {
                    timeSinceLastOutput.Restart();
                }

                lines.AppendLine(line);
            }
        }
    }
}
