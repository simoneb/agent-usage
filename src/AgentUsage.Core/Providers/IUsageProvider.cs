namespace AgentUsage.Providers;

/// <summary>
/// One tool's way of answering "how much of my subscription have I used".
///
/// The deliberate constraint on anything implementing this: it may run a CLI the user installed,
/// or read a file that CLI wrote, and nothing else. No browser cookies, no undocumented endpoints,
/// no credential files. Every competitor gets its breadth by breaking that rule, and every one of
/// them is one silent API change away from reporting a confident wrong number.
/// </summary>
public interface IUsageProvider
{
    /// <summary>The id used in config, e.g. "claude".</summary>
    string Id { get; }

    /// <summary>How this provider is named in the UI.</summary>
    string DisplayName { get; }

    Task<AccountStatus> ProbeAsync(AccountConfig account, ProbeContext context, CancellationToken ct);
}

/// <summary>
/// What a probe is allowed to know about the wider app: resolved executable paths, and whatever
/// this provider previously learned that is worth not re-establishing on every poll.
/// </summary>
public sealed class ProbeContext
{
    public string? ClaudePath { get; init; }

    /// <summary>
    /// A previously confirmed sign-in for this profile. Identity does not change between polls,
    /// so re-running `auth status` every time only costs another process launch. The caller
    /// drops its cached value whenever a probe fails, which is when it could be stale.
    /// </summary>
    public AuthStatus? KnownAuth { get; init; }

    public TimeSpan Timeout { get; init; } = ProcessRunner.DefaultTimeout;
}

public static class ProviderRegistry
{
    private static readonly IUsageProvider[] Known =
    {
        new ClaudeProvider(),
        new CodexProvider(),
    };

    public static IUsageProvider? Find(string? id)
    {
        var normalised = ProviderIds.Normalise(id);

        return Array.Find(Known, p => string.Equals(p.Id, normalised, StringComparison.OrdinalIgnoreCase));
    }
}
