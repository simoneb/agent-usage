using System.Globalization;

namespace AgentUsage;

/// <summary>
/// What a limit counts. Providers do not agree on this: Claude and Codex report a percentage of
/// a rolling window, Copilot a count of premium requests per calendar month, Gemini requests per
/// day, OpenRouter money against a cap. A model that only understood percentages would force
/// every future provider to be rounded into one, which is how you end up displaying a confident
/// number that is not true.
/// </summary>
public enum LimitKind
{
    Percent,
    Count,
    Currency,
}

/// <summary>A single limit window, e.g. "Current week (all models)".</summary>
public sealed record LimitRow
{
    public required string Label { get; init; }

    public LimitKind Kind { get; init; } = LimitKind.Percent;

    /// <summary>How much is used, in whatever <see cref="Kind"/> says this is.</summary>
    public double Value { get; init; }

    /// <summary>The ceiling, when the provider states one. Null means "used, out of unknown".</summary>
    public double? Max { get; init; }

    /// <summary>Currency code or item name — "USD", "requests". Null for percentages.</summary>
    public string? Unit { get; init; }

    /// <summary>The reset stamp exactly as the provider worded it, when it only gives text.</summary>
    public string? Resets { get; init; }

    /// <summary>An unambiguous reset moment, when the provider gives one. Preferred over
    /// <see cref="Resets"/>, which has to be parsed out of prose that could change wording.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>
    /// True when this window had already reset by the time the reading was taken — so
    /// <see cref="Value"/> describes a window that no longer exists.
    ///
    /// This is the normal end state of any provider whose figures are read from a file rather
    /// than asked for: leave Codex alone for a week and its last recorded "93% of your weekly
    /// limit" describes a week that has since rolled over. Reporting that number would be worse
    /// than reporting nothing, because it looks exactly like a fresh one.
    /// </summary>
    public bool Expired { get; init; }

    /// <summary>
    /// The 0-100 figure the bars and the icon need, or null when this limit cannot honestly
    /// produce one. Two cases: a count with no stated maximum — "412 requests" is real
    /// information and inventing a denominator to draw a bar with would not be — and a window
    /// that has already reset.
    /// </summary>
    public int? Percent => Expired ? null : Kind switch
    {
        LimitKind.Percent => (int)Math.Round(Value),
        _ when Max is > 0 => (int)Math.Round(Value / Max.Value * 100),
        _ => null,
    };

    /// <summary>
    /// The value as a reader should see it, whatever shape it is.
    ///
    /// Formatted invariantly on purpose. This string is part of the --json contract, and a
    /// status bar on an it-IT machine parsing "$12,40" out of a field that reads "$12.40"
    /// everywhere else is a bug nobody would find quickly.
    /// </summary>
    public string Display => Expired ? "stale" : Kind switch
    {
        LimitKind.Percent => string.Create(Culture, $"{(int)Math.Round(Value)}%"),
        LimitKind.Currency when Max is not null => $"{Money(Value)} / {Money(Max.Value)}",
        LimitKind.Currency => Money(Value),
        _ when Max is not null => string.Create(Culture, $"{Value:0.##} / {Max.Value:0.##}"),
        _ => string.Create(Culture, $"{Value:0.##}"),
    };

    private static CultureInfo Culture => CultureInfo.InvariantCulture;

    private string Money(double amount) => Unit is null or "USD"
        ? string.Create(Culture, $"${amount:0.00}")
        : string.Create(Culture, $"{amount:0.00} {Unit}");

    /// <summary>A percentage window, which is what both current providers report.</summary>
    public static LimitRow Percentage(
        string label,
        double percent,
        string? resets = null,
        DateTimeOffset? resetsAt = null,
        DateTimeOffset? asOf = null) => new()
    {
        Label = label,
        Kind = LimitKind.Percent,
        Value = percent,
        Max = 100,
        Resets = resets,
        ResetsAt = resetsAt,
        Expired = asOf is DateTimeOffset now && resetsAt is DateTimeOffset at && at <= now,
    };
}

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

    /// <summary>When this reading was taken.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// When the underlying data was last refreshed by the provider itself, where that differs
    /// from when we read it. Codex only writes rate limits while you are using Codex, so a
    /// reading can be perfectly successful and hours old — and the reader has to be told.
    /// </summary>
    public DateTime? MeasuredAt { get; set; }

    public string Provider => ProviderIds.Normalise(Account.Provider);

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

    /// <summary>The row whose label contains <paramref name="fragment"/>, whatever shape it is.</summary>
    public LimitRow? LimitFor(string fragment)
    {
        foreach (var l in Limits)
            if (l.Label.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return l;

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
                    best = Larger(best, l.Percent);
            }

            if (best is not null) return best;

            // Nothing weekly: the worst window this account has. Rows that cannot produce a
            // percentage are skipped rather than counted as zero.
            foreach (var l in Limits) best = Larger(best, l.Percent);

            return best;
        }
    }

    private static int? Larger(int? current, int? candidate) =>
        candidate is null ? current : current is null ? candidate : Math.Max(current.Value, candidate.Value);
}
