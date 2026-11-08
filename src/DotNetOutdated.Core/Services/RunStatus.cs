using System.Linq;

namespace DotNetOutdated.Core.Services
{
    public class RunStatus
    {
        public string Output { get; }
        public string Errors { get; }
        public int ExitCode { get; }

        public bool IsSuccess => ExitCode == 0;

        /// <summary>
        /// The process output followed by its errors, trimmed and with empty parts left out, for explaining a failure.
        /// </summary>
        /// <remarks>
        /// Both streams are needed: <c>dotnet add package</c>, for example, reports NuGet failures (such as a package
        /// whose version is declared in an imported file it cannot edit) on stdout rather than stderr.
        /// </remarks>
        public string FailureDetails => string.Join(
            System.Environment.NewLine,
            new[] { Output?.Trim(), Errors?.Trim() }.Where(part => !string.IsNullOrEmpty(part)));

        public RunStatus(string output, string errors, int exitCode)
        {
            Output = output;
            Errors = errors;
            ExitCode = exitCode;
        }
    }
}
