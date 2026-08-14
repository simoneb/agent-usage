using ClaudeUsageWidget;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The `/usage` text layout is the one thing here that is not a contract. These tests pin the
/// shapes the widget must keep understanding, and the shapes it must refuse rather than
/// misread — a wrong percentage is worse than no percentage.
/// </summary>
public class ParseLimitsTests
{
    private const string RealOutput = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 2% used · resets Aug 15, 3am (Europe/Rome)
        Current week (all models): 93% used · resets Aug 15, 12pm (Europe/Rome)
        Current week (Fable): 63% used · resets Aug 15, 12pm (Europe/Rome)

        What's contributing to your limits usage?
        Approximate, based on local sessions on this machine.

        Last 24h · 301 requests · 2 sessions
          100% of your usage was at >150k context
        """;

    [Fact]
    public void ReadsEveryLimitRowFromRealOutput()
    {
        var rows = UsageProbe.ParseLimits(RealOutput);

        Assert.Equal(3, rows.Count);

        Assert.Equal("Current session", rows[0].Label);
        Assert.Equal(2, rows[0].Percent);
        Assert.Equal("Aug 15, 3am (Europe/Rome)", rows[0].Resets);

        Assert.Equal("Current week (all models)", rows[1].Label);
        Assert.Equal(93, rows[1].Percent);

        Assert.Equal("Current week (Fable)", rows[2].Label);
        Assert.Equal(63, rows[2].Percent);
    }

    [Fact]
    public void IgnoresThePercentagesInTheContributingSection()
    {
        // "100% of your usage was at >150k context" is a percentage on its own line and would
        // be a plausible false positive for a looser pattern.
        var rows = UsageProbe.ParseLimits(RealOutput);

        Assert.DoesNotContain(rows, r => r.Percent == 100);
    }

    [Fact]
    public void PicksUpLimitRowsThisVersionHasNeverSeen()
    {
        // New model-specific windows appear without warning; they must not need a code change.
        var rows = UsageProbe.ParseLimits(
            "Current week (Some Future Model): 12% used · resets Sep 1, 9am (UTC)");

        var row = Assert.Single(rows);
        Assert.Equal("Current week (Some Future Model)", row.Label);
        Assert.Equal(12, row.Percent);
    }

    [Fact]
    public void AcceptsARowWithNoResetTime()
    {
        var row = Assert.Single(UsageProbe.ParseLimits("Current session: 40% used"));

        Assert.Equal(40, row.Percent);
        Assert.Null(row.Resets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("You are currently using your subscription to power your Claude Code usage")]
    [InlineData("Session usage: 40%")]                    // renamed label
    [InlineData("Current session: forty percent used")]   // non-numeric
    public void ReturnsNothingRatherThanGuessing(string text)
    {
        Assert.Empty(UsageProbe.ParseLimits(text));
    }
}
