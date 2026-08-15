namespace AgentUsage;

public static class Readings
{
    /// <summary>
    /// Keeps the readings that still belong to a configured account, in config order, and
    /// forgets the rest.
    ///
    /// Readings and config are separate things that drift apart the moment config changes: the
    /// panel draws from the last completed poll, so an account removed from the file keeps its
    /// old numbers on screen until a poll happens to finish. To the person who just deleted it
    /// and pressed Reload, that is indistinguishable from the reload not working — and the
    /// numbers still sitting there are real ones, which makes it worse, not better.
    /// </summary>
    public static AccountStatus[] ForAccounts(
        IReadOnlyList<AccountConfig> accounts, IReadOnlyList<AccountStatus> existing)
    {
        var kept = new List<AccountStatus>(accounts.Count);

        foreach (var account in accounts)
        {
            foreach (var status in existing)
            {
                if (!SameAccount(status.Account, account)) continue;

                kept.Add(status);
                break;
            }
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Whether two entries describe the same account. Compared by what identifies it rather than
    /// by reference: config is reloaded into fresh objects, so every reading would otherwise be
    /// discarded on every reload and the panel would blink through "Loading…" each time.
    /// </summary>
    public static bool SameAccount(AccountConfig a, AccountConfig b) =>
        string.Equals(a.Label, b.Label, StringComparison.Ordinal) &&
        string.Equals(
            ProviderIds.Normalise(a.Provider), ProviderIds.Normalise(b.Provider),
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ConfigDir, b.ConfigDir, StringComparison.OrdinalIgnoreCase);
}
