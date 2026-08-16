using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentUsage.Providers;

/// <summary>
/// Reads Codex limits out of the session transcript Codex itself writes.
///
/// Codex has no equivalent of `claude -p "/usage"` — the numbers are only shown by `/status`
/// inside the TUI (openai/codex#15281 asks for `codex usage --json`). But every turn Codex
/// records the rate limits the API returned into its rollout file, so the figure is already on
/// disk. Reading it costs no process, no tokens and no credentials.
///
/// The catch, which the UI must not hide: this number is only as fresh as your last Codex turn.
/// A reading from Tuesday is reported as a reading from Tuesday, never as "0% used".
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public string Id => ProviderIds.Codex;
    public string DisplayName => "Codex";

    /// <summary>Codex's own override for its home directory.</summary>
    public const string HomeVariable = "CODEX_HOME";

    /// <summary>
    /// How much of the newest rollout file to read. Rate limits are recorded on every turn, so
    /// the last record is always near the end; a long session can otherwise run to megabytes
    /// and we would re-read all of it every poll.
    /// </summary>
    private const int TailBytes = 512 * 1024;

    public Task<AccountStatus> ProbeAsync(
        AccountConfig account, ProbeContext context, CancellationToken ct)
    {
        var status = new AccountStatus { Account = account, UpdatedAt = DateTime.Now };

        try
        {
            var home = ResolveHome(account.ConfigDir);
            var sessions = Path.Combine(home, "sessions");

            if (!Directory.Exists(sessions))
            {
                status.Error = $"no Codex sessions found in {home}";
                return Task.FromResult(status);
            }

            var reading = FindLatestReading(sessions);
            if (reading is null)
            {
                status.Error = "no Codex usage recorded yet — run Codex once";
                return Task.FromResult(status);
            }

            var (line, newest) = reading.Value;

            var evt = ParseEvent(line);
            var limits = evt?.Payload?.RateLimits;

            if (limits is null)
            {
                status.Error = "could not read Codex rate limits — transcript format may have changed";
                return Task.FromResult(status);
            }

            var rows = MapLimits(limits, DateTimeOffset.Now);
            if (rows.Count == 0)
            {
                status.Error = "Codex reported no usage windows";
                return Task.FromResult(status);
            }

            // Codex is signed in by definition here: it could not have written a transcript
            // otherwise. Plan type is the only identity it records.
            status.LoggedIn = true;
            status.SubscriptionType = limits.PlanType;
            status.Limits = rows;
            status.MeasuredAt = evt?.MeasuredAt ?? newest.LastWriteTime;

            return Task.FromResult(status);
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            return Task.FromResult(status);
        }
    }

    /// <summary>CODEX_HOME wins, then an explicit config dir, then Codex's default.</summary>
    public static string ResolveHome(string? configured)
    {
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } fromEnv)
            return fromEnv;

        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>
    /// How many transcripts back to look before giving up. More than one because the newest file
    /// is not always the one with the answer: starting a Codex session creates a transcript
    /// immediately, but nothing records a rate limit until the first turn comes back. Reading
    /// only the newest file would blank the widget out for as long as you sat at a fresh prompt.
    /// </summary>
    private const int TranscriptsToTry = 5;

    /// <summary>The most recent reading and the transcript it came from, newest first.</summary>
    public static (string Line, FileInfo File)? FindLatestReading(string sessionsDir)
    {
        var rollouts = new List<FileInfo>();

        foreach (var path in Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl",
                     SearchOption.AllDirectories))
            rollouts.Add(new FileInfo(path));

        rollouts.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        var tried = 0;

        foreach (var file in rollouts)
        {
            if (tried++ >= TranscriptsToTry) break;

            string? line;

            try
            {
                line = FindLastRateLimitLine(file.FullName);
            }
            catch (IOException)
            {
                // Being written, or gone since the enumeration. Try the next one.
                continue;
            }

            if (line is not null) return (line, file);
        }

        return null;
    }

    /// <summary>
    /// The last line of the transcript carrying rate limits, scanning backwards. Opened
    /// share-all because Codex may well be writing to this file right now.
    /// </summary>
    public static string? FindLastRateLimitLine(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        var take = (int)Math.Min(length, TailBytes);
        stream.Seek(length - take, SeekOrigin.Begin);

        var buffer = new byte[take];
        stream.ReadExactly(buffer, 0, take);

        var text = Encoding.UTF8.GetString(buffer);
        var lines = text.Split('\n');

        // Backwards, so the newest reading wins. The first line may be a fragment left by the
        // tail cut; it simply fails to parse and is skipped like any other non-match.
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                continue;

            if (ParseEvent(line)?.Payload?.RateLimits is not null) return line;
        }

        return null;
    }

    public static CodexEvent? ParseEvent(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, CoreJson.Default.CodexEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns Codex's two anonymous windows into labelled rows. Codex names them "primary" and
    /// "secondary" and only describes them by length, so the label comes from the window itself —
    /// which also means a plan with different windows describes itself correctly without a change
    /// here.
    /// </summary>
    public static List<LimitRow> MapLimits(CodexRateLimits limits, DateTimeOffset now)
    {
        var rows = new List<LimitRow>();

        Add(limits.Primary);
        Add(limits.Secondary);

        return rows;

        void Add(CodexWindow? window)
        {
            if (window is null) return;

            rows.Add(LimitRow.Percentage(
                LabelFor(window.WindowMinutes),
                window.UsedPercent,
                resetsAt: window.ResetsAt is long unix and > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : null,
                asOf: now,
                // Codex is the one provider that states how long its windows are, so nothing has
                // to be inferred from the label here.
                window: window.WindowMinutes is int minutes and > 0
                    ? TimeSpan.FromMinutes(minutes)
                    : null));
        }
    }

    /// <summary>
    /// Labels chosen to match the Claude vocabulary — "session" and "week" — so one panel can
    /// show both providers without the reader having to learn two names for the same horizon.
    /// </summary>
    public static string LabelFor(int? windowMinutes) => windowMinutes switch
    {
        null => "Current window",
        <= 0 => "Current window",
        300 => "Current session",
        10080 => "Current week",
        < 1440 => $"Current session ({Round(windowMinutes.Value / 60.0)}h)",
        _ => $"Current window ({Round(windowMinutes.Value / 1440.0)}d)",
    };

    private static string Round(double value) => value.ToString("0.#");
}

// ---- transcript shapes -------------------------------------------------
//
// Unofficial: these are Codex's internal rollout records, not a published contract. Everything is
// nullable and a mismatch produces an error rather than a zero, because the alternative is a
// widget confidently reporting that you have used none of your week.

public sealed class CodexEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("payload")] public CodexPayload? Payload { get; set; }

    /// <summary>
    /// When Codex recorded this reading. Preferred over the file's modification time, which
    /// moves for reasons that have nothing to do with the numbers — a copy, a backup tool, a
    /// sync client.
    /// </summary>
    public DateTime? MeasuredAt =>
        DateTimeOffset.TryParse(Timestamp, out var parsed) ? parsed.LocalDateTime : null;
}

public sealed class CodexPayload
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("rate_limits")] public CodexRateLimits? RateLimits { get; set; }
}

public sealed class CodexRateLimits
{
    [JsonPropertyName("primary")] public CodexWindow? Primary { get; set; }
    [JsonPropertyName("secondary")] public CodexWindow? Secondary { get; set; }
    [JsonPropertyName("plan_type")] public string? PlanType { get; set; }

    // Three more fields ride along here and are all deliberately unmodelled:
    //
    //   credits: { has_credits, unlimited, balance }
    //   individual_limit
    //   spend_control_reached
    //
    // Credits is the interesting one, and the reason it is not a row yet is direction. Every
    // limit in this model counts what has been *used*; a credit balance counts what is *left*.
    // Rendering one as the other inverts it — 5% of your credits remaining would draw as 5%
    // used, in green. Every sample seen so far has balance: null on a plan with no credits, so
    // there is nothing to confirm the units or the direction against, and a guess here fails in
    // the worst possible way: quietly, and reassuringly.
}

public sealed class CodexWindow
{
    [JsonPropertyName("used_percent")] public double UsedPercent { get; set; }
    [JsonPropertyName("window_minutes")] public int? WindowMinutes { get; set; }
    [JsonPropertyName("resets_at")] public long? ResetsAt { get; set; }
}
