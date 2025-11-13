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
    public class DotNetRunner : IDotNetRunner
    {
        public RunStatus Run(string workingDirectory, string[] arguments, int commandTimeOut = 20000)
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
                        if (timeSinceLastOutput.ElapsedMilliseconds > commandTimeOut) {
                            break;
                        }
                    }

                    Thread.Sleep(100);
                }

                if (!processExited)
                {
                    p.Kill();

                    return new RunStatus(output.ToString(), errors.ToString(), exitCode: -1);
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
