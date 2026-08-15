using System.Runtime.InteropServices;
using AgentUsage.Widget;
using Xunit;

namespace AgentUsage.Widget.Tests;

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

    [Theory]
    [InlineData("0.8.0.0", "Agent Usage v0.8.0")]
    [InlineData("1.2.3.4", "Agent Usage v1.2.3")]
    [InlineData("0.8.0", "Agent Usage v0.8.0")]
    public void NamesTheRunningBuildTheWayItsTagDoes(string current, string expected)
    {
        // The menu line sits directly above "Update to v0.9.0", so the two have to be
        // comparable at a glance — a fourth component here reads as a different scheme.
        Assert.Equal(expected, Updates.MenuTitle(Version.Parse(current)));
    }

    [Fact]
    public void SaysNothingAboutAVersionItCannotRead()
    {
        // Same rule as the update check itself: no reading is better than a made-up one.
        Assert.Equal("Agent Usage", Updates.MenuTitle(null));
    }

    [Theory]
    [InlineData(Architecture.X64, "AgentUsageWidget-win-x64.exe")]
    [InlineData(Architecture.Arm64, "AgentUsageWidget-win-arm64.exe")]
    public void DownloadsTheBuildThisMachineCanRun(Architecture arch, string expected)
    {
        Assert.Equal(expected, Updates.AssetNameFor(arch));
    }

    [Fact]
    public void RefusesToGuessAtAnArchitectureThatIsNotShipped()
    {
        // Better to leave the release page as the only route than to install an x64 binary on
        // something that cannot execute it.
        Assert.Null(Updates.AssetNameFor(Architecture.X86));
    }

    /// <summary>
    /// The checksum is the whole reason the swap is safe to do unattended: a truncated download
    /// or a body that is not the asset at all has to fail here, before anything is renamed.
    /// </summary>
    public class Checksums
    {
        private const string Sums = """
            b2eb0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b  AgentUsageWidget-win-arm64.exe
            3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d  AgentUsageWidget-win-x64.exe
            0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff  agent-usage-linux-x64
            """;

        [Fact]
        public void PicksTheLineForTheAssetBeingInstalled()
        {
            Assert.Equal("3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d3f5a9e2d",
                Updates.HashFor(Sums, "AgentUsageWidget-win-x64.exe"));
        }

        [Fact]
        public void DoesNotMatchAnAssetOnAPrefix()
        {
            // "AgentUsageWidget-win-arm64.exe" ends with the same characters a careless
            // Contains() would accept for the x64 name and vice versa.
            Assert.Equal("b2eb0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b0c0a1b",
                Updates.HashFor(Sums, "AgentUsageWidget-win-arm64.exe"));
        }

        [Fact]
        public void ReportsNothingForAnAssetTheReleaseDoesNotList()
        {
            Assert.Null(Updates.HashFor(Sums, "AgentUsageWidget-win-x86.exe"));
        }

        [Fact]
        public void ReadsTheBinaryModeMarkerSomeToolsWrite()
        {
            Assert.Equal("0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff",
                Updates.HashFor("0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff *widget.exe",
                    "widget.exe"));
        }
    }

    /// <summary>
    /// The URL comes out of a JSON payload and what it points at is about to be run as this
    /// application. Anywhere but GitHub is refused, whatever the release says.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/simoneb/agent-usage/releases/download/v1.0.0/x.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x", true)]
    [InlineData("https://api.github.com/repos/simoneb/agent-usage/releases/assets/1", true)]
    [InlineData("http://github.com/simoneb/agent-usage/releases/download/v1.0.0/x.exe", false)]
    [InlineData("https://github.com.example.net/simoneb/agent-usage/x.exe", false)]
    [InlineData("https://notgithub.com/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("nonsense", false)]
    public void OnlyTrustsGitHubsOwnHosts(string url, bool trusted)
    {
        Assert.Equal(trusted, Updates.IsTrustedDownload(url));
    }
}
