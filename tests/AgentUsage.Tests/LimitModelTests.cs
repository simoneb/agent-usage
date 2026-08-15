using Xunit;

namespace AgentUsage.Tests;

/// <summary>
/// Providers do not agree on what a limit is. These pin the rule that keeps that from turning
/// into a wrong number on screen: a bar is only ever drawn from a figure that genuinely is a
/// proportion of something.
/// </summary>
public class LimitModelTests
{
    [Fact]
    public void APercentageIsItsOwnPercentage()
    {
        var row = LimitRow.Percentage("Current week", 93);

        Assert.Equal(93, row.Percent);
        Assert.Equal("93%", row.Display);
    }

    [Fact]
    public void ACountAgainstACeilingBecomesAPercentage()
    {
        // Copilot's shape: premium requests used out of a monthly allowance.
        var row = new LimitRow
        {
            Label = "Premium requests",
            Kind = LimitKind.Count,
            Value = 300,
            Max = 1500,
            Unit = "requests",
        };

        Assert.Equal(20, row.Percent);
        Assert.Equal("300 / 1500", row.Display);
    }

    [Fact]
    public void ACountWithNoCeilingHasNoPercentage()
    {
        // The number is real and worth showing; the denominator is not ours to invent.
        var row = new LimitRow { Label = "Requests today", Kind = LimitKind.Count, Value = 412 };

        Assert.Null(row.Percent);
        Assert.Equal("412", row.Display);
    }

    [Fact]
    public void MoneyReadsAsMoney()
    {
        var row = new LimitRow
        {
            Label = "Spend",
            Kind = LimitKind.Currency,
            Value = 12.4,
            Max = 20,
            Unit = "USD",
        };

        Assert.Equal("$12.40 / $20.00", row.Display);
        Assert.Equal(62, row.Percent);
    }

    [Fact]
    public void TheHeadlineIgnoresRowsThatCannotProduceANumber()
    {
        var status = new AccountStatus
        {
            Account = new AccountConfig { Label = "codex", Provider = ProviderIds.Codex },
            Limits = new[]
            {
                LimitRow.Percentage("Current week", 93, resetsAt: DateTimeOffset.UtcNow.AddDays(-1),
                    asOf: DateTimeOffset.UtcNow),
                LimitRow.Percentage("Current session", 12, resetsAt: DateTimeOffset.UtcNow.AddHours(2),
                    asOf: DateTimeOffset.UtcNow),
            },
        };

        // The weekly row expired, so the only honest headline is the session one — not the 93
        // that would otherwise win on both "prefers weekly" and "worst window".
        Assert.Equal(12, status.HeadlinePercent);
    }

    [Fact]
    public void AnAccountWhoseEveryWindowExpiredHasNoHeadlineAtAll()
    {
        var status = new AccountStatus
        {
            Account = new AccountConfig { Label = "codex", Provider = ProviderIds.Codex },
            Limits = new[]
            {
                LimitRow.Percentage("Current week", 93, resetsAt: DateTimeOffset.UtcNow.AddDays(-1),
                    asOf: DateTimeOffset.UtcNow),
            },
        };

        // Null is what the icon renders as "–". A zero here would read as "none used".
        Assert.Null(status.HeadlinePercent);
    }
}

public class ConfigTests
{
    [Fact]
    public void AnAccountWithNoProviderIsAClaudeAccount()
    {
        // Every config written before there was a second provider looks like this.
        var config = new AppConfig { Accounts = { new AccountConfig { Label = "work", Provider = "" } } };

        Assert.True(config.Normalise());
        Assert.Equal(ProviderIds.Claude, config.Accounts[0].Provider);
    }

    [Fact]
    public void ProviderNamesAreMatchedLoosely()
    {
        var config = new AppConfig { Accounts = { new AccountConfig { Provider = " CODEX " } } };

        config.Normalise();

        Assert.Equal(ProviderIds.Codex, config.Accounts[0].Provider);
    }

    [Fact]
    public void AnUnknownProviderIsKeptSoTheErrorCanNameIt()
    {
        var config = new AppConfig { Accounts = { new AccountConfig { Provider = "gemini" } } };

        config.Normalise();

        Assert.Equal("gemini", config.Accounts[0].Provider);
        Assert.False(ProviderIds.IsKnown("gemini"));
    }

    [Fact]
    public void PollingFasterThanTheClisCanAnswerIsClamped()
    {
        var config = new AppConfig { PollSeconds = 2, Accounts = { new AccountConfig() } };

        Assert.True(config.Normalise());
        Assert.Equal(10, config.PollSeconds);
    }
}

public class SnapshotTests
{
    [Fact]
    public void CarriesTheFieldsAStatusBarNeeds()
    {
        var status = new AccountStatus
        {
            Account = new AccountConfig { Label = "work", Provider = ProviderIds.Claude },
            LoggedIn = true,
            Email = "someone@example.com",
            SubscriptionType = "max",
            MeasuredAt = DateTime.Now,
            Limits = new[] { LimitRow.Percentage("Current week (all models)", 42) },
        };

        var snapshot = Snapshot.From(new[] { status }, DateTimeOffset.Now);
        var account = Assert.Single(snapshot.Accounts);

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("claude", account.Provider);
        Assert.Equal(42, account.HeadlinePercent);
        Assert.Equal("42%", account.Headline!.Display);
        Assert.Equal("percent", account.Headline.Kind);
    }

    [Fact]
    public void OffersNoHeadlineWhenThereIsNoHonestNumber()
    {
        var status = new AccountStatus
        {
            Account = new AccountConfig { Label = "codex", Provider = ProviderIds.Codex },
            Error = "no Codex sessions found",
        };

        var account = Assert.Single(Snapshot.From(new[] { status }, DateTimeOffset.Now).Accounts);

        Assert.Null(account.HeadlinePercent);
        Assert.Null(account.Headline);
        Assert.Equal("no Codex sessions found", account.Error);
    }

    [Fact]
    public void SerialisesWithoutReflection()
    {
        // The real assertion is that this does not throw: reflection-based JSON is disabled, so a
        // type missing from the source-generated context fails here rather than on a user's Mac.
        var snapshot = Snapshot.From(
            new[]
            {
                new AccountStatus
                {
                    Account = new AccountConfig { Label = "work" },
                    Limits = new[] { LimitRow.Percentage("Current session", 5) },
                },
            },
            DateTimeOffset.Now);

        var json = snapshot.ToJson();

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"display\": \"5%\"", json);
    }
}
