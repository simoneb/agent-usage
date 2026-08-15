using ClaudeUsageWidget;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The comparison is the whole feature: announcing an update that is not one, or staying quiet
/// about one that is, are both worse than not checking at all.
/// </summary>
public class UpdatesTests
{
    [Theory]
    [InlineData("v0.6.0", "0.5.0.0", true)]
    [InlineData("v1.0.0", "0.9.9.0", true)]
    [InlineData("v0.5.1", "0.5.0.0", true)]
    [InlineData("v0.5.0", "0.5.0.0", false)]   // the release we are running
    [InlineData("v0.4.0", "0.5.0.0", false)]   // a rollback is not an update
    public void RecognisesOnlyAGenuinelyNewerTag(string tag, string current, bool expected)
    {
        Assert.Equal(expected, Updates.IsNewer(tag, Version.Parse(current)));
    }

    [Fact]
    public void DoesNotMistakeAnAbsentRevisionForAnOlderRelease()
    {
        // A tag parses to three components and the binary's resource to four. Version treats a
        // missing revision as -1, so a naive comparison calls 0.5.0 older than 0.5.0.0 and the
        // app would announce an "update" to the build already running.
        Assert.False(Updates.IsNewer("v0.5.0", new Version(0, 5, 0, 0)));
    }

    [Theory]
    [InlineData("v0.5.0")]
    [InlineData("V0.5.0")]
    [InlineData("0.5.0")]
    public void AcceptsTagsWithOrWithoutTheLeadingV(string tag)
    {
        Assert.True(Updates.TryParseTag(tag, out var version));
        Assert.Equal(new Version(0, 5, 0), version);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("")]
    public void StaysQuietOnATagItCannotRead(string tag)
    {
        Assert.False(Updates.IsNewer(tag, new Version(0, 5, 0, 0)));
    }

    [Fact]
    public void StaysQuietWhenItCannotTellWhatIsRunning()
    {
        Assert.False(Updates.IsNewer("v9.9.9", null));
    }
}
