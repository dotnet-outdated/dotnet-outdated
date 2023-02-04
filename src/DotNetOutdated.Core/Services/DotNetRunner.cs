using System.Diagnostics;
using System.IO;
using System.Text;
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
        public RunStatus Run(string workingDirectory, string[] arguments)
        {
            var psi = new ProcessStartInfo("dotnet", string.Join(" ", arguments))
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
                var outputTask = ConsumeStreamReaderAsync(p.StandardOutput, output);
                var errorTask = ConsumeStreamReaderAsync(p.StandardError, errors);

                var processExited = p.WaitForExit(20000);

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

        private static async Task ConsumeStreamReaderAsync(StreamReader reader, StringBuilder lines)
        {
            await Task.Yield();

            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lines.AppendLine(line);
            }
        }
    }
}
