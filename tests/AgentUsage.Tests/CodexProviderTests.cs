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

    // The same record from CLI 0.147.0, nineteen releases later. Kept alongside rather than
    // replacing the older one: both are shapes in the wild, and the point of reading an
    // unpublished format is knowing the moment it stops being the one you read.
    private const string RealEvent0147 =
        """
        {"timestamp":"2026-08-15T11:42:58.001Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":14749,"total_tokens":14749},"model_context_window":258400},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":0.0,"window_minutes":43200,"resets_at":1789386180},"secondary":null,"credits":{"has_credits":false,"unlimited":false,"balance":null},"individual_limit":null,"spend_control_reached":null,"plan_type":"free","rate_limit_reached_type":null}}}
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
    public void ReadsTheShapeTheCurrentCodexWrites()
    {
        // 0.147.0 added credits, individual_limit and spend_control_reached to this record.
        // Nothing we read moved, and unknown fields are ignored rather than fatal — which is
        // the property that lets a transcript format evolve without breaking the widget.
        var limits = CodexProvider.ParseEvent(RealEvent0147)?.Payload?.RateLimits;

        Assert.NotNull(limits);
        Assert.Equal("free", limits.PlanType);
        Assert.Equal(0.0, limits.Primary!.UsedPercent);
        Assert.Equal(43200, limits.Primary.WindowMinutes);
        Assert.Null(limits.Secondary);
    }

    [Fact]
    public void HandlesAWindowLengthItWasNeverWrittenFor()
    {
        // The plan behind the 0.147.0 sample reports a 30-day window — neither of the two
        // horizons this was designed around. Naming windows by their length rather than by a
        // fixed list is what stops that needing a code change.
        var limits = CodexProvider.ParseEvent(RealEvent0147)!.Payload!.RateLimits!;

        var row = Assert.Single(CodexProvider.MapLimits(limits, DateTimeOffset.UnixEpoch));

        Assert.Equal("Current window (30d)", row.Label);
        Assert.Equal(0, row.Percent);
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
    public void SaysWhatIsWrongWhenTheClaudeCliIsNotThere()
    {
        // This is the state of every machine that has not installed Claude Code, including every
        // CI runner. The runtime's own words for it are "ErrorStartingProcess, claude, /tmp/,
        // No such file or directory", which explains the mechanism and not the problem.
        var account = new AccountConfig { Label = "work", Provider = ProviderIds.Claude };

        var context = new ProbeContext
        {
            ClaudePath = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-claude-binary"),
        };

        var status = new ClaudeProvider()
            .ProbeAsync(account, context, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotNull(status.Error);
        Assert.Contains("is Claude Code installed", status.Error);
        Assert.Empty(status.Limits);
        Assert.Null(status.HeadlinePercent);
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
