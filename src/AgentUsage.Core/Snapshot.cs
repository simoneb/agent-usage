using System.Text.Json;

namespace AgentUsage;

/// <summary>
/// The public shape of `agent-usage --json`. Anything reading this — a waybar module, an xbar
/// plugin, a shell prompt, someone's dashboard — is a consumer we cannot see and must not break,
/// so this type is a contract rather than a convenience: fields are added, never repurposed, and
/// <see cref="SchemaVersion"/> moves if one ever has to change meaning.
/// </summary>
public sealed class Snapshot
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset GeneratedAt { get; set; }

    public List<SnapshotAccount> Accounts { get; set; } = new();

    public static Snapshot From(IEnumerable<AccountStatus> statuses, DateTimeOffset now)
    {
        var snapshot = new Snapshot { GeneratedAt = now };

        foreach (var status in statuses)
        {
            var account = new SnapshotAccount
            {
                Provider = status.Provider,
                Label = status.Account.Label,
                LoggedIn = status.LoggedIn,
                Email = status.Email,
                Plan = status.SubscriptionType,
                Error = status.Error,
                MeasuredAt = status.MeasuredAt,
                AgeSeconds = status.MeasuredAt is DateTime at
                    ? Math.Max(0, Math.Round((now.LocalDateTime - at).TotalSeconds))
                    : null,
            };

            foreach (var limit in status.Limits)
                account.Limits.Add(SnapshotLimit.From(limit));

            // The one number a status bar with room for one number should show.
            if (status.HeadlinePercent is int headline)
            {
                account.HeadlinePercent = headline;

                foreach (var limit in status.Limits)
                {
                    if (limit.Percent == headline)
                    {
                        account.Headline = SnapshotLimit.From(limit);
                        break;
                    }
                }
            }

            snapshot.Accounts.Add(account);
        }

        return snapshot;
    }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJson.Default.Snapshot);
}

public sealed class SnapshotAccount
{
    public string Provider { get; set; } = ProviderIds.Claude;
    public string Label { get; set; } = "default";

    public bool LoggedIn { get; set; }
    public string? Email { get; set; }
    public string? Plan { get; set; }

    /// <summary>Null when the reading succeeded. Consumers should show this instead of numbers.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// When the provider itself last measured this. For Codex that can be hours ago even on a
    /// perfectly successful read, which is why it is reported rather than assumed to be now.
    /// </summary>
    public DateTimeOffset? MeasuredAt { get; set; }

    public double? AgeSeconds { get; set; }

    public int? HeadlinePercent { get; set; }
    public SnapshotLimit? Headline { get; set; }

    public List<SnapshotLimit> Limits { get; set; } = new();
}

public sealed class SnapshotLimit
{
    public string Label { get; set; } = string.Empty;

    /// <summary>"percent", "count" or "currency" — what <see cref="Value"/> counts.</summary>
    public string Kind { get; set; } = "percent";

    public double Value { get; set; }
    public double? Max { get; set; }
    public string? Unit { get; set; }

    /// <summary>0-100 where the limit can honestly produce one, null where it cannot.</summary>
    public int? Percent { get; set; }

    /// <summary>
    /// The window had already reset when this was read, so <see cref="Value"/> describes a
    /// window that is over. Consumers should show staleness, not the number.
    /// </summary>
    public bool Expired { get; set; }

    /// <summary>Preformatted for display: "93%", "412 / 1500", "$12.40".</summary>
    public string Display { get; set; } = string.Empty;

    public string? Resets { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }

    public static SnapshotLimit From(LimitRow row) => new()
    {
        Label = row.Label,
        Kind = row.Kind switch
        {
            LimitKind.Count => "count",
            LimitKind.Currency => "currency",
            _ => "percent",
        },
        Value = row.Value,
        Max = row.Max,
        Unit = row.Unit,
        Percent = row.Percent,
        Expired = row.Expired,
        Display = row.Display,
        Resets = row.Resets,
        ResetsAt = row.ResetsAt,
    };
}
