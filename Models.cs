using System.Text.Json.Serialization;

namespace ClaudeUsageWidget;

/// <summary>One Claude Code account to poll. ConfigDir maps to CLAUDE_CONFIG_DIR.</summary>
public sealed class AccountConfig
{
    public string Label { get; set; } = "default";

    /// <summary>Null means "use whatever profile the CLI picks by default".</summary>
    public string? ConfigDir { get; set; }
}

public sealed class AppConfig
{
    public int PollSeconds { get; set; } = 30;

    /// <summary>Explicit path to claude.exe. Null resolves from PATH.</summary>
    public string? ClaudePath { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// Whether to ask GitHub once a day whether a newer release exists. Off means the app makes
    /// no network requests of its own at all.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Which limit the icon and taskbar bar report, matched against a row label — "session",
    /// "all models", a model name. Null shows everything the icon has room for.
    /// </summary>
    public string? IconLimit { get; set; }

    /// <summary>
    /// Last panel position. Null means "not placed yet". Nullable rather than a -1 sentinel
    /// because monitors left of or above the primary one have genuinely negative coordinates.
    /// </summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    public List<AccountConfig> Accounts { get; set; } = new();

    public static AppConfig Default() => new()
    {
        Accounts = { new AccountConfig { Label = "default", ConfigDir = null } }
    };

    /// <summary>
    /// Brings a loaded config into range. Returns true when something changed, so the caller
    /// can write the corrected version back.
    /// </summary>
    public bool Normalise()
    {
        var changed = false;

        if (Accounts.Count == 0)
        {
            Accounts = Default().Accounts;
            changed = true;
        }

        if (PollSeconds <= 0)
        {
            PollSeconds = 30;
            changed = true;
        }

        // Each poll spawns the CLI, which takes roughly two and a half seconds. Below ten
        // seconds the widget would spend most of its life starting processes.
        if (PollSeconds < 10)
        {
            PollSeconds = 10;
            changed = true;
        }

        return changed;
    }
}

/// <summary>A single limit window as reported by /usage, e.g. "Current week (all models)".</summary>
public sealed record LimitRow(string Label, int Percent, string? Resets);

public sealed class AccountStatus
{
    public required AccountConfig Account { get; init; }

    public bool LoggedIn { get; set; }
    public string? Email { get; set; }
    public string? OrgName { get; set; }
    public string? SubscriptionType { get; set; }

    public IReadOnlyList<LimitRow> Limits { get; set; } = Array.Empty<LimitRow>();

    /// <summary>Non-null when the probe failed; the widget shows this instead of stale numbers.</summary>
    public string? Error { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The current session window. A different horizon from the weekly figure rather than a
    /// smaller version of it — it can sit above or below the week at any time.
    /// </summary>
    public int? SessionPercent
    {
        get
        {
            foreach (var l in Limits)
                if (l.Label.Contains("session", StringComparison.OrdinalIgnoreCase))
                    return l.Percent;

            return null;
        }
    }

    /// <summary>The row whose label contains <paramref name="fragment"/>, if this account has one.</summary>
    public int? PercentFor(string fragment)
    {
        foreach (var l in Limits)
            if (l.Label.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return l.Percent;

        return null;
    }

    /// <summary>The number the taskbar shows. Prefers the all-models weekly row.</summary>
    public int? HeadlinePercent
    {
        get
        {
            foreach (var l in Limits)
            {
                if (l.Label.Contains("week", StringComparison.OrdinalIgnoreCase) &&
                    l.Label.Contains("all models", StringComparison.OrdinalIgnoreCase))
                    return l.Percent;
            }

            int? best = null;
            foreach (var l in Limits)
            {
                if (l.Label.Contains("week", StringComparison.OrdinalIgnoreCase))
                    best = best is null ? l.Percent : Math.Max(best.Value, l.Percent);
            }

            if (best is not null) return best;

            foreach (var l in Limits)
                best = best is null ? l.Percent : Math.Max(best.Value, l.Percent);

            return best;
        }
    }
}

/// <summary>Shape of `claude auth status --json`.</summary>
public sealed class AuthStatus
{
    [JsonPropertyName("loggedIn")] public bool LoggedIn { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("orgName")] public string? OrgName { get; set; }
    [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; set; }
}

/// <summary>The one field of the GitHub release payload this needs.</summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
}

/// <summary>Envelope of `claude -p ... --output-format json`. Only the fields we need.</summary>
public sealed class CliResult
{
    [JsonPropertyName("is_error")] public bool IsError { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
}

// Source-generated serialisation: reflection-based JSON is disabled under NativeAOT.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AuthStatus))]
[JsonSerializable(typeof(CliResult))]
[JsonSerializable(typeof(ReleaseInfo))]
internal partial class JsonContext : JsonSerializerContext;
