using McMaster.Extensions.CommandLineUtils;
using NuGet.Common;
using System;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services;

public class ConsoleLogger : ILogger
{
    private readonly LogLevel _minimumLogLevel;
    private readonly IConsole _console;

    public ConsoleLogger(IConsole console, LogLevel minimumLogLevel)
    {
        _minimumLogLevel = minimumLogLevel;
        _console = console;
    }

    public void LogDebug(string data)
    {
        Log(new LogMessage(LogLevel.Debug, data));
    }

    public void LogVerbose(string data)
    {
        Log(new LogMessage(LogLevel.Verbose, data));
    }

    public void LogInformation(string data)
    {
        Log(new LogMessage(LogLevel.Information, data));
    }

    public void LogMinimal(string data)
    {
        Log(new LogMessage(LogLevel.Minimal, data));
    }

    public void LogWarning(string data)
    {
        Log(new LogMessage(LogLevel.Warning, data));
    }

    public void LogError(string data)
    {
        Log(new LogMessage(LogLevel.Error, data));
    }

    public void LogInformationSummary(string data)
    {
        Log(new LogMessage(LogLevel.Information, data));
    }

    public void Log(LogLevel level, string data)
    {
        Log(new LogMessage(level, data));
    }

    public Task LogAsync(LogLevel level, string data)
    {
        return LogAsync(new LogMessage(level, data));
    }

    public void Log(ILogMessage message)
    {
        // Ignore if Log is lower level than minimum
        if (_minimumLogLevel > message.Level)
        {
            return;
        }

        // Save ForegroundColor so we can return it after the log has been written
        var color = _console.ForegroundColor;

        switch (message.Level)
        {
            case LogLevel.Warning:
                _console.ForegroundColor = ConsoleColor.Yellow;
                break;
            case LogLevel.Error:
                _console.ForegroundColor = ConsoleColor.Red;
                break;
            default:
                break;
        }

        _console.WriteLine("[{0}] {1}", message.Level.ToString().ToUpper(), message.Message);
        _console.ForegroundColor = color;
    }

    public Task LogAsync(ILogMessage message)
    {
        Log(message);

        return Task.CompletedTask;
    }
}