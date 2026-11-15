using System;
using DotNetOutdated.Core.Services;
using Xunit;

namespace DotNetOutdated.Tests;

public class RunStatusTests
{
    [Theory]
    [InlineData("out\n", "err\n", "out|err")]
    [InlineData("out\n", "", "out")]
    [InlineData("  \n", "err", "err")]
    [InlineData("", "", "")]
    [InlineData(null, null, "")]
    public void FailureDetails_JoinsTrimmedOutputThenErrors_SkippingEmptyParts(string output, string errors, string expected)
    {
        var status = new RunStatus(output, errors, exitCode: 1);

        Assert.Equal(expected.Replace("|", Environment.NewLine), status.FailureDetails);
    }

    [Fact]
    public void Constructor_TreatsMissingStreamsAsEmpty()
    {
        var status = new RunStatus(null, null, exitCode: 0);

        Assert.Equal(string.Empty, status.Output);
        Assert.Equal(string.Empty, status.Errors);
    }
}
