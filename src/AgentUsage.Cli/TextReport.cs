using System.Text;

namespace AgentUsage.Cli;

/// <summary>
/// What a person sees when they run this in a terminal. Deliberately not the JSON with the
/// braces taken out: a reader wants the limits stacked and aligned, and wants to be told when a
/// reading is old rather than having to subtract two timestamps.
/// </summary>
internal static class TextReport
{
    /// <summary>
    /// One line, for the places that only have one line: a menu bar title, a tmux status, a
    /// shell prompt. Kept in the binary rather than left to the caller's jq so that the smallest
    /// useful integration is a one-line shell script with no dependencies at all.
    /// </summary>
    public static string RenderBrief(Snapshot snapshot)
    {
        if (snapshot.Accounts.Count == 0) return "no accounts";

        var parts = new List<string>();

        foreach (var account in snapshot.Accounts)
        {
            var value = account switch
            {
                { Error: { Length: > 0 } } => "!",
                { Headline: { } headline } => headline.Display,
                { Limits.Count: 0 } => "?",
                _ => account.Limits[0].Display,
            };

            parts.Add($"{account.Label} {value}");
        }

        return string.Join(" · ", parts);
    }

    public static string Render(Snapshot snapshot)
    {
        if (snapshot.Accounts.Count == 0) return "No accounts configured.";

        var output = new StringBuilder();

        for (var i = 0; i < snapshot.Accounts.Count; i++)
        {
            if (i > 0) output.AppendLine();
            RenderAccount(output, snapshot.Accounts[i], snapshot.GeneratedAt);
        }

        return output.ToString().TrimEnd();
    }

    private static void RenderAccount(StringBuilder output, SnapshotAccount account, DateTimeOffset now)
    {
        var heading = new StringBuilder($"{account.Label} · {account.Provider}");

        if (account.Plan is { Length: > 0 } plan) heading.Append($" · {plan}");

        // Only worth saying when it is genuinely old. Codex readings are written when you use
        // Codex, so "3d ago" is a normal state and the reader has to know they are looking at it.
        if (account.AgeSeconds is > 90 and double age)
            heading.Append($"  (measured {Age(age)} ago)");

        output.AppendLine(heading.ToString());

        if (account.Error is { Length: > 0 } error)
        {
            output.AppendLine($"  {error}");
            return;
        }

        if (account.Limits.Count == 0)
        {
            output.AppendLine("  no limits reported");
            return;
        }

        var labelWidth = 0;
        var valueWidth = 0;

        foreach (var limit in account.Limits)
        {
            labelWidth = Math.Max(labelWidth, Short(limit.Label).Length);
            valueWidth = Math.Max(valueWidth, limit.Display.Length);
        }

        foreach (var limit in account.Limits)
        {
            var line = new StringBuilder(
                $"  {Short(limit.Label).PadRight(labelWidth)}  {limit.Display.PadLeft(valueWidth)}");

            var resets = Resets(limit, now);
            if (resets is not null) line.Append($"   resets {resets}");

            output.AppendLine(line.ToString());
        }
    }

    private static string? Resets(SnapshotLimit limit, DateTimeOffset now)
    {
        // A window that has already rolled over has no reset worth counting down to, and
        // "resets any moment" would read as though the number beside it were current.
        if (limit.Expired) return null;

        if (limit.ResetsAt is DateTimeOffset at) return ResetTime.Describe(at, now);

        return limit.Resets is { Length: > 0 } text
            ? ResetTime.Describe(text, now.LocalDateTime)
            : null;
    }

    /// <summary>"Current week (all models)" is noise in a column of them.</summary>
    private static string Short(string label)
    {
        if (label.Contains("session", StringComparison.OrdinalIgnoreCase)) return "session";
        if (label.Contains("all models", StringComparison.OrdinalIgnoreCase)) return "week";

        var open = label.IndexOf('(');
        var close = label.IndexOf(')');
        if (open >= 0 && close > open) return label[(open + 1)..close].ToLowerInvariant();

        return label.Replace("Current ", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static string Age(double seconds) => seconds switch
    {
        < 3600 => $"{(int)(seconds / 60)}m",
        < 86400 => $"{(int)(seconds / 3600)}h",
        _ => $"{(int)(seconds / 86400)}d",
    };
}
