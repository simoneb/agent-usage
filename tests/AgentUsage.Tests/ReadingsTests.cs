using Xunit;

namespace AgentUsage.Tests;

/// <summary>
/// Readings and config drift apart the moment config changes, and the panel draws from readings.
/// These pin the reconciliation that keeps an edit to the file looking like it took effect.
/// </summary>
public class ReadingsTests
{
    private static AccountConfig Account(string label, string provider = "claude", string? dir = null) =>
        new() { Label = label, Provider = provider, ConfigDir = dir };

    private static AccountStatus Reading(AccountConfig account, int percent) => new()
    {
        Account = account,
        LoggedIn = true,
        Limits = new[] { LimitRow.Percentage("Current week (all models)", percent) },
    };

    [Fact]
    public void ForgetsAnAccountThatHasBeenRemovedFromConfig()
    {
        // The bug this exists for: deleting an account and reloading left its last numbers on
        // the panel, looking live, until some later poll happened to finish.
        var work = Account("work");
        var codex = Account("codex", ProviderIds.Codex);

        var kept = Readings.ForAccounts(
            new[] { work },
            new[] { Reading(work, 8), Reading(codex, 3) });

        var only = Assert.Single(kept);
        Assert.Equal("work", only.Account.Label);
    }

    [Fact]
    public void KeepsReadingsAcrossAReloadThatChangedNothing()
    {
        // Config is reloaded into fresh objects, so identity has to be by value — otherwise
        // every reload discards every reading and the panel blinks through "Loading…".
        var before = Account("work");
        var after = Account("work");

        var kept = Readings.ForAccounts(new[] { after }, new[] { Reading(before, 42) });

        Assert.Equal(42, Assert.Single(kept).HeadlinePercent);
    }

    [Fact]
    public void ReordersToMatchConfigSoThePanelIsTheIconsLegend()
    {
        // The icon tells accounts apart by position in config order. If the panel and the icon
        // disagreed about that order, the icon would be lying about which bar is which.
        var work = Account("work");
        var personal = Account("personal");

        var kept = Readings.ForAccounts(
            new[] { personal, work },
            new[] { Reading(work, 8), Reading(personal, 90) });

        Assert.Collection(kept,
            first => Assert.Equal("personal", first.Account.Label),
            second => Assert.Equal("work", second.Account.Label));
    }

    [Fact]
    public void HasNothingToShowForAnAccountAddedButNotYetPolled()
    {
        // A new account contributes no row until it has been read. Better an absent row for a
        // couple of seconds than an invented one reading zero.
        var work = Account("work");

        var kept = Readings.ForAccounts(
            new[] { work, Account("brand-new") },
            new[] { Reading(work, 8) });

        Assert.Single(kept);
    }

    [Theory]
    [InlineData("work", "claude", null, "work", "claude", null, true)]
    [InlineData("work", "claude", null, "work", "codex", null, false)]   // same label, other tool
    [InlineData("work", "claude", null, "personal", "claude", null, false)]
    [InlineData("work", "claude", "C:\\a", "work", "claude", "C:\\b", false)]
    [InlineData("work", "CLAUDE", null, "work", "claude", null, true)]   // provider case
    public void TellsAccountsApartByWhatIdentifiesThem(
        string labelA, string providerA, string? dirA,
        string labelB, string providerB, string? dirB,
        bool same)
    {
        Assert.Equal(same, Readings.SameAccount(
            Account(labelA, providerA, dirA), Account(labelB, providerB, dirB)));
    }
}
