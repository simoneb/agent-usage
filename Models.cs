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
    public int PollMinutes { get; set; } = 5;

    /// <summary>Explicit path to claude.exe. Null resolves from PATH.</summary>
    public string? ClaudePath { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

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
internal partial class JsonContext : JsonSerializerContext;
