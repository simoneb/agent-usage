using ClaudeUsageWidget;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// Reset stamps come from the same unstable text as the percentages, and a countdown built on a
/// misread stamp is worse than the stamp itself. These pin the shapes the CLI emits and the
/// rule for when counting down stops being the clearer answer.
/// </summary>
public class ResetTimeTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 0, 30, 0);

    [Theory]
    [InlineData("Aug 15, 2:59am (Europe/Rome)", 2, 59)]
    [InlineData("Aug 15, 3am (Europe/Rome)", 3, 0)]        // minutes omitted at the hour
    [InlineData("Aug 15, 11:59am", 11, 59)]
    [InlineData("Aug 15, 12pm", 12, 0)]
    [InlineData("Aug 15, 8:59pm", 20, 59)]
    public void ReadsTheShapesTheCliEmits(string text, int hour, int minute)
    {
        Assert.True(ResetTime.TryParse(text, Now, out var at));

        Assert.Equal(new DateTime(2026, 8, 15, hour, minute, 0), at);
    }

    [Fact]
    public void RollsIntoNextYearRatherThanReportingAResetInThePast()
    {
        // A reset always lies ahead, so a December stamp read in January is next December's.
        var january = new DateTime(2026, 1, 2, 9, 0, 0);

        Assert.True(ResetTime.TryParse("Dec 28, 9am", january, out var at));
        Assert.Equal(2026, at.Year);
        Assert.True(at > january);
    }

    [Theory]
    [InlineData("Aug 15, 1:30am", "in 1h")]
    [InlineData("Aug 15, 3:50am", "in 3h 20m")]
    [InlineData("Aug 15, 1am", "in 30m")]
    public void CountsDownWhileTheResetIsNear(string text, string expected)
    {
        Assert.Equal(expected, ResetTime.Describe(text, Now));
    }

    [Fact]
    public void SaysUnderAMinuteRatherThanRoundingDownToNothing()
    {
        // Stamps carry whole minutes, so this is only reachable partway through one.
        var now = new DateTime(2026, 8, 15, 0, 30, 30);

        Assert.Equal("in under a minute", ResetTime.Describe("Aug 15, 12:31am", now));
    }

    [Fact]
    public void KeepsTheDateOnceCountingDownStopsHelping()
    {
        // Six days out, "in 143h" answers nothing a date does not answer better.
        Assert.Equal("Aug 21, 8:59pm", ResetTime.Describe("Aug 21, 8:59pm (Europe/Rome)", Now));
    }

    [Fact]
    public void FallsBackToTheStampItCannotRead()
    {
        // A stamp this code cannot parse is still one the reader can.
        Assert.Equal("sometime on Friday",
            ResetTime.Describe("sometime on Friday (Europe/Rome)", Now));
    }

    [Fact]
    public void DoesNotClaimTimeRemainsOnAResetThatHasPassed()
    {
        Assert.Equal("any moment", ResetTime.Describe("Aug 15, 12:29am", Now));
    }
}
