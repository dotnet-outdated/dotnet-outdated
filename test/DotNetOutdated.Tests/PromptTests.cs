using Xunit;

namespace DotNetOutdated.Tests;

/// <remarks>
/// The expectations were recorded from McMaster.Extensions.CommandLineUtils' <c>Prompt.GetYesNo</c>, which this
/// replaced, so the upgrade prompt reads and behaves as it did, except at the end of the input: that is not an
/// answer, so it gives <see langword="null"/> rather than the default (#831).
/// </remarks>
public class PromptTests
{
    private const string Question = "Do you wish to upgrade this package?";

    public static TheoryData<string, bool, bool?, string> Answers() => new()
    {
        // input, default answer, expected answer, expected output
        { "", true, null, Ask(true) },
        { "", false, null, Ask(false) },
        { "\n", true, true, Ask(true) },
        { "\n", false, false, Ask(false) },
        { "y\n", true, true, Ask(true) },
        { "y\n", false, true, Ask(false) },
        { "Y\n", true, true, Ask(true) },
        { "Y\n", false, true, Ask(false) },
        { "yes\n", true, true, Ask(true) },
        { "yes\n", false, true, Ask(false) },
        { " YES \n", true, true, Ask(true) },
        { " YES \n", false, true, Ask(false) },
        { "n\n", true, false, Ask(true) },
        { "n\n", false, false, Ask(false) },
        { "No\n", true, false, Ask(true) },
        { "No\n", false, false, Ask(false) },
        { "NO", true, false, Ask(true) },
        { "NO", false, false, Ask(false) },
        { "maybe\ny\n", true, true, Ask(true) + Invalid("maybe") + Ask(true) },
        { "maybe\ny\n", false, true, Ask(false) + Invalid("maybe") + Ask(false) },
        { "maybe\n\n", true, true, Ask(true) + Invalid("maybe") + Ask(true) },
        { "maybe\n\n", false, false, Ask(false) + Invalid("maybe") + Ask(false) },
        { "x\nz\nn\n", true, false, Ask(true) + Invalid("x") + Ask(true) + Invalid("z") + Ask(true) },
        { "x\nz\nn\n", false, false, Ask(false) + Invalid("x") + Ask(false) + Invalid("z") + Ask(false) },
        { "x\n", true, null, Ask(true) + Invalid("x") + Ask(true) },
        { "x\n", false, null, Ask(false) + Invalid("x") + Ask(false) },
        { "x", true, null, Ask(true) + Invalid("x") + Ask(true) },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public void GetYesNo(string input, bool defaultAnswer, bool? expectedAnswer, string expectedOutput)
    {
        var console = new RecordingConsole(input);

        var answer = Prompt.GetYesNo(console, Question, defaultAnswer);

        Assert.Equal(expectedAnswer, answer);
        Assert.Equal(expectedOutput, console.Transcript);
    }

    private static string Ask(bool defaultAnswer) => $"{Question} {(defaultAnswer ? "[Y/n]" : "[y/N]")} ";

    private static string Invalid(string response) =>
        $"Invalid response '{response}'. Please answer 'y' or 'n' or CTRL+C to exit.\n";
}
