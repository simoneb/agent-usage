namespace AgentUsage;

/// <summary>
/// One account to poll. <see cref="ConfigDir"/> is that provider's home directory —
/// CLAUDE_CONFIG_DIR for Claude, CODEX_HOME for Codex — so a machine with several profiles
/// lists each one here.
/// </summary>
public sealed class AccountConfig
{
    public string Label { get; set; } = "default";

    /// <summary>
    /// Which tool this account belongs to. Absent means Claude, so configs written before
    /// there was anything else keep loading unchanged.
    /// </summary>
    public string Provider { get; set; } = ProviderIds.Claude;

    /// <summary>Null means "use whatever profile the tool picks by default".</summary>
    public string? ConfigDir { get; set; }
}

/// <summary>The provider ids accepted in config. Strings rather than an enum: this is a value
/// a human types into a JSON file, and an unknown one has to survive round-tripping.</summary>
public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";

    public static readonly string[] All = { Claude, Codex };

    public static bool IsKnown(string? provider) =>
        provider is not null && Array.Exists(All, p => Matches(p, provider));

    public static string Normalise(string? provider) =>
        provider is null ? Claude : Array.Find(All, p => Matches(p, provider)) ?? provider.Trim();

    private static bool Matches(string known, string candidate) =>
        string.Equals(known, candidate.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class AppConfig
{
    public int PollSeconds { get; set; } = 30;

    /// <summary>Explicit path to the Claude CLI. Null resolves from PATH.</summary>
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
        Accounts = { new AccountConfig { Label = "default", Provider = ProviderIds.Claude } }
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

        foreach (var account in Accounts)
        {
            // An empty or absent provider is Claude — that was the only option when these
            // configs were written. An unrecognised one is left alone so the error names what
            // the user actually typed.
            var normalised = string.IsNullOrWhiteSpace(account.Provider)
                ? ProviderIds.Claude
                : ProviderIds.Normalise(account.Provider);

            if (normalised != account.Provider)
            {
                account.Provider = normalised;
                changed = true;
            }
        }

        if (PollSeconds <= 0)
        {
            PollSeconds = 30;
            changed = true;
        }

        // A Claude poll spawns the CLI, which takes roughly two and a half seconds. Below ten
        // seconds the widget would spend most of its life starting processes.
        if (PollSeconds < 10)
        {
            PollSeconds = 10;
            changed = true;
        }

        return changed;
    }
}
