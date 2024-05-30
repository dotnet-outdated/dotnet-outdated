using System;
using System.Threading.Tasks;
using NuGet.Common;

namespace DotNetOutdated.Core.Services
{
    public class SimpleProblemLogger : ILogger
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
            if (message.Level is LogLevel.Warning || message.Level is LogLevel.Error)
            {
                var color = Console.ForegroundColor;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(message.Message);

                Console.ForegroundColor = color;
            }
        }

        public Task LogAsync(ILogMessage message)
        {
            return Task.CompletedTask;
        }
    }    
}