using Xunit;

namespace DotNetOutdated.Tests;

/// <remarks>
/// Matches McMaster.Extensions.CommandLineUtils' <c>ConsoleReporter</c>, which this replaced: the colour is set,
/// the line written, then the colour reset (not restored).
/// </remarks>
public class ConsoleReporterTests
{
    [Theory]
    [InlineData("hello", "[fg:Yellow]hello\n[reset]")]
    [InlineData("", "[fg:Yellow]\n[reset]")]
    [InlineData("two\nlines", "[fg:Yellow]two\nlines\n[reset]")]
    public void Warn_WritesToStandardOutputInYellow(string message, string expected)
    {
        var console = new RecordingConsole();

        new ConsoleReporter(console).Warn(message);

        Assert.Equal(expected, console.Transcript);
    }

    [Theory]
    [InlineData("hello", "[fg:Red]{err}hello\n[reset]")]
    [InlineData("", "[fg:Red]{err}\n[reset]")]
    [InlineData("two\nlines", "[fg:Red]{err}two\nlines\n[reset]")]
    public void Error_WritesToStandardErrorInRed(string message, string expected)
    {
        var console = new RecordingConsole();

        new ConsoleReporter(console).Error(message);

        Assert.Equal(expected, console.Transcript);
    }
}
