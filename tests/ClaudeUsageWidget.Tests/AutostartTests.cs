using ClaudeUsageWidget;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The registry side is a few lines around a well-known key; the part that can silently be
/// wrong is deciding whether a stored command refers to this exe.
/// </summary>
public class AutostartTests
{
    private const string Exe = @"C:\Users\you\AppData\Local\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe";

    [Fact]
    public void QuotesThePathSoTheShellDoesNotSplitItOnASpace()
    {
        Assert.Equal("\"C:\\Program Files\\App\\app.exe\"",
            Autostart.Quote(@"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void LeavesAnAlreadyQuotedPathAlone()
    {
        Assert.Equal("\"C:\\app.exe\"", Autostart.Quote("\"C:\\app.exe\""));
    }

    [Theory]
    [InlineData("\"" + Exe + "\"")]      // as written
    [InlineData(Exe)]                     // written by hand without quotes
    [InlineData("  \"" + Exe + "\"  ")]   // padded
    public void RecognisesItsOwnCommandHoweverItWasWritten(string command)
    {
        Assert.True(Autostart.SamePath(command, Exe));
    }

    [Fact]
    public void IsCaseInsensitiveTheWayWindowsPathsAre()
    {
        Assert.True(Autostart.SamePath("\"" + Exe.ToUpperInvariant() + "\"", Exe));
    }

    [Fact]
    public void TreatsAValueLeftByACopyThatMovedAsOff()
    {
        // Reporting "on" here would put a checkmark on a path that starts nothing. Off means
        // clicking the item repairs the entry instead.
        Assert.False(Autostart.SamePath(@"""D:\dev\claude-usage-widget\dist\ClaudeUsageWidget.exe""", Exe));
    }

    [Fact]
    public void NeverMatchesWhenTheRunningPathIsUnknown()
    {
        Assert.False(Autostart.SamePath("\"" + Exe + "\"", null));
    }
}
