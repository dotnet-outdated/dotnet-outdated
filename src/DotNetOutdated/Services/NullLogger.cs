using System.Threading.Tasks;
using NuGet.Common;

namespace DotNetOutdated.Services
{
    public class NullLogger : ILogger
    {
        public void LogDebug(string data)
        {
        }

        public void LogVerbose(string data)
        {
        }

        public void LogInformation(string data)
        {
        }

        public void LogMinimal(string data)
        {
        }

        public void LogWarning(string data)
        {
        }

        public void LogError(string data)
        {
        }

        public void LogInformationSummary(string data)
        {
        }

        public void Log(LogLevel level, string data)
        {
        }

        public Task LogAsync(LogLevel level, string data)
        {
            return Task.CompletedTask;
        }

        public void Log(ILogMessage message)
        {
        }

        public Task LogAsync(ILogMessage message)
        {
            return Task.CompletedTask;
        }
    }
}