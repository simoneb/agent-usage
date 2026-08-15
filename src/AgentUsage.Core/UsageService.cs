using AgentUsage.Providers;

namespace AgentUsage;

/// <summary>
/// Polls every configured account. Shared by the widget and the CLI so the two can never drift
/// into disagreeing about what your usage is.
/// </summary>
public static class UsageService
{
    public static Task<AccountStatus[]> ProbeAllAsync(
        AppConfig config,
        Func<AccountConfig, AuthStatus?>? knownAuth,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var claudePath = ClaudeProvider.ResolveClaudePath(config.ClaudePath);

        var probes = Array.ConvertAll(config.Accounts.ToArray(), account =>
        {
            var provider = ProviderRegistry.Find(account.Provider);

            if (provider is null)
            {
                return Task.FromResult(new AccountStatus
                {
                    Account = account,
                    UpdatedAt = DateTime.Now,
                    Error = $"unknown provider \"{account.Provider}\" — expected one of: " +
                            string.Join(", ", ProviderIds.All),
                });
            }

            var context = new ProbeContext
            {
                ClaudePath = claudePath,
                KnownAuth = knownAuth?.Invoke(account),
                Timeout = timeout ?? ProcessRunner.DefaultTimeout,
            };

            return provider.ProbeAsync(account, context, ct);
        });

        // Separate processes and separate config directories, so nothing is gained by serialising.
        return Task.WhenAll(probes);
    }
}
