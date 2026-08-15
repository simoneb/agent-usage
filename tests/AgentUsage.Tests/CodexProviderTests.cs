using AgentUsage.Providers;
using Xunit;

namespace AgentUsage.Tests;

/// <summary>
/// Codex's rollout transcript is not a published contract — it is an internal record we read
/// because the numbers are in it. That makes it exactly the thing that breaks silently, so these
/// pin the shape observed in the wild and, more importantly, pin what happens when it stops
/// matching: an error, never a number.
/// </summary>
public class CodexProviderTests
{
    // Copied verbatim from a real ~/.codex/sessions/**/rollout-*.jsonl, CLI 0.128.0.
    private const string RealEvent =
        """
        {"timestamp":"2026-05-01T12:04:41.160Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1778241881},"secondary":null,"credits":null,"plan_type":"free","rate_limit_reached_type":null}}}
        """;

    private static readonly DateTimeOffset BeforeReset = DateTimeOffset.FromUnixTimeSeconds(1778241881).AddDays(-1);
    private static readonly DateTimeOffset AfterReset = DateTimeOffset.FromUnixTimeSeconds(1778241881).AddDays(1);

    [Fact]
    public void ReadsTheShapeCodexActuallyWrites()
    {
        var limits = CodexProvider.ParseEvent(RealEvent)?.Payload?.RateLimits;

        Assert.NotNull(limits);
        Assert.Equal("free", limits.PlanType);
        Assert.Equal(3.0, limits.Primary!.UsedPercent);
        Assert.Equal(10080, limits.Primary.WindowMinutes);
        Assert.Null(limits.Secondary);
    }

    [Fact]
    public void TakesTheEventTimestampAsWhenTheReadingWasMeasured()
    {
        var measured = CodexProvider.ParseEvent(RealEvent)!.MeasuredAt;

        Assert.Equal(
            new DateTimeOffset(2026, 5, 1, 12, 4, 41, 160, TimeSpan.Zero).LocalDateTime,
            measured);
    }

    [Fact]
    public void TurnsAWindowIntoALabelledRowWithItsResetMoment()
    {
        var limits = CodexProvider.ParseEvent(RealEvent)!.Payload!.RateLimits!;

        var row = Assert.Single(CodexProvider.MapLimits(limits, BeforeReset));

        Assert.Equal("Current week", row.Label);
        Assert.Equal(3, row.Percent);
        Assert.Equal("3%", row.Display);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1778241881), row.ResetsAt);
    }

    [Fact]
    public void RefusesToReportAWindowThatHasAlreadyReset()
    {
        // The failure this exists to prevent: Codex only writes limits while you use Codex, so a
        // fortnight-old "93% of your week" would otherwise render as a live 93%.
        var limits = CodexProvider.ParseEvent(RealEvent)!.Payload!.RateLimits!;

        var row = Assert.Single(CodexProvider.MapLimits(limits, AfterReset));

        Assert.True(row.Expired);
        Assert.Null(row.Percent);
        Assert.Equal("stale", row.Display);
    }

    [Theory]
    [InlineData(300, "Current session")]
    [InlineData(10080, "Current week")]
    [InlineData(60, "Current session (1h)")]
    [InlineData(4320, "Current window (3d)")]
    [InlineData(null, "Current window")]
    public void NamesWindowsByTheirLengthSoANewPlanNeedsNoCodeChange(int? minutes, string expected)
    {
        Assert.Equal(expected, CodexProvider.LabelFor(minutes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","info":null}}""")]
    public void ReturnsNothingRatherThanGuessing(string line)
    {
        Assert.Null(CodexProvider.ParseEvent(line)?.Payload?.RateLimits);
    }

    [Fact]
    public void TakesTheNewestReadingInATranscript()
    {
        var path = Path.GetTempFileName();

        try
        {
            var older = RealEvent.Replace("\"used_percent\":3.0", "\"used_percent\":11.0");
            var newer = RealEvent.Replace("\"used_percent\":3.0", "\"used_percent\":47.0");

            File.WriteAllLines(path, new[]
            {
                """{"type":"session_meta","payload":{"id":"whatever"}}""",
                older,
                """{"type":"event_msg","payload":{"type":"agent_message"}}""",
                newer,
            });

            var line = CodexProvider.FindLastRateLimitLine(path);

            Assert.NotNull(line);
            Assert.Equal(47.0, CodexProvider.ParseEvent(line)!.Payload!.RateLimits!.Primary!.UsedPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LooksPastAFreshTranscriptThatHasNotRecordedAnythingYet()
    {
        // Starting a Codex session creates a transcript immediately, but no rate limit is
        // recorded until the first turn returns. Reading only the newest file would blank the
        // reading out for as long as you sat at a fresh prompt.
        var dir = Directory.CreateTempSubdirectory("agent-usage-tests");

        try
        {
            var withLimits = Path.Combine(dir.FullName, "rollout-2026-08-15T10-00-00-aaa.jsonl");
            File.WriteAllLines(withLimits, new[] { RealEvent });
            File.SetLastWriteTimeUtc(withLimits, new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc));

            var justStarted = Path.Combine(dir.FullName, "rollout-2026-08-15T11-00-00-bbb.jsonl");
            File.WriteAllLines(justStarted, new[] { """{"type":"session_meta","payload":{"id":"new"}}""" });
            File.SetLastWriteTimeUtc(justStarted, new DateTime(2026, 8, 15, 11, 0, 0, DateTimeKind.Utc));

            var reading = CodexProvider.FindLatestReading(dir.FullName);

            Assert.NotNull(reading);
            Assert.Equal(withLimits, reading.Value.File.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SurvivesATranscriptWithNoLimitsInIt()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllLines(path, new[] { """{"type":"session_meta","payload":{}}""" });

            Assert.Null(CodexProvider.FindLastRateLimitLine(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportsAnErrorRatherThanZeroWhenThereIsNothingToRead()
    {
        var account = new AccountConfig
        {
            Label = "codex",
            Provider = ProviderIds.Codex,
            ConfigDir = Path.Combine(Path.GetTempPath(), "agent-usage-tests-no-such-codex-home"),
        };

        // CODEX_HOME wins over the config dir, so it has to be cleared for the test to be about
        // the directory it names.
        var saved = Environment.GetEnvironmentVariable(CodexProvider.HomeVariable);
        Environment.SetEnvironmentVariable(CodexProvider.HomeVariable, null);

        try
        {
            var status = new CodexProvider()
                .ProbeAsync(account, new ProbeContext(), CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.NotNull(status.Error);
            Assert.Empty(status.Limits);
            Assert.Null(status.HeadlinePercent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexProvider.HomeVariable, saved);
        }
    }
}
