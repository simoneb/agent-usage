using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace AgentUsage.Providers;

/// <summary>
/// Runs the Claude Code CLI to read subscription limits. No credentials are read or held here —
/// the CLI owns auth and token refresh. `/usage` is handled locally by the CLI: zero turns,
/// zero inference cost.
/// </summary>
public sealed partial class ClaudeProvider : IUsageProvider
{
    public string Id => ProviderIds.Claude;
    public string DisplayName => "Claude Code";

    // Matches every limit line /usage prints, so new limit rows are picked up without a code change:
    //   "Current session: 0% used · resets Aug 15, 2:59am (Europe/Rome)"
    //   "Current week (all models): 93% used · resets Aug 15, 11:59am (Europe/Rome)"
    [GeneratedRegex(@"^(Current (?:session|week[^:]*)):\s*(\d+)%\s*used(?:\s*[·-]\s*resets\s+(.+?))?\s*$",
        RegexOptions.Multiline)]
    private static partial Regex LimitLine();

    public async Task<AccountStatus> ProbeAsync(
        AccountConfig account, ProbeContext context, CancellationToken ct)
    {
        var status = new AccountStatus { Account = account, UpdatedAt = DateTime.Now };
        var claudePath = context.ClaudePath ?? ResolveClaudePath(null);

        var env = account.ConfigDir is { Length: > 0 } dir
            ? new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = dir }
            : null;

        try
        {
            var authStatus = context.KnownAuth;

            if (authStatus is null)
            {
                var auth = await ProcessRunner.RunAsync(
                    claudePath, "auth status --json", env, context.Timeout, ct);

                // Exit code is non-zero when signed out, but the JSON body is still valid —
                // parse first.
                authStatus = TryDeserialize(auth.StdOut, CoreJson.Default.AuthStatus);
                if (authStatus is null)
                {
                    status.Error = Describe("auth status", auth);
                    return status;
                }
            }

            status.LoggedIn = authStatus.LoggedIn;
            status.Email = authStatus.Email;
            status.OrgName = authStatus.OrgName;
            status.SubscriptionType = authStatus.SubscriptionType;

            if (!status.LoggedIn)
            {
                status.Error = "signed out — run `claude auth login` for this profile";
                return status;
            }

            var usage = await ProcessRunner.RunAsync(
                claudePath, "-p \"/usage\" --output-format json", env, context.Timeout, ct);

            var cli = TryDeserialize(usage.StdOut, CoreJson.Default.CliResult);
            if (cli is null || cli.IsError || string.IsNullOrWhiteSpace(cli.Result))
            {
                status.Error = Describe("/usage", usage);
                return status;
            }

            var limits = ParseLimits(cli.Result);
            if (limits.Count == 0)
            {
                // The text layout is not a stable contract. Fail loud rather than show a wrong number.
                status.Error = "could not parse /usage output — CLI format may have changed";
                return status;
            }

            status.Limits = limits;
            status.MeasuredAt = status.UpdatedAt;
            return status;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status.Error = $"timed out after {context.Timeout.TotalSeconds:0}s";
            return status;
        }
        catch (Win32Exception)
        {
            // What the runtime says here is "ErrorStartingProcess, claude, /tmp/, No such file or
            // directory", which describes the mechanism rather than the problem. The problem is
            // almost always that Claude Code is not installed, and that is what to say.
            status.Error = $"could not run \"{claudePath}\" — is Claude Code installed and on PATH?";
            return status;
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            return status;
        }
    }

    public static List<LimitRow> ParseLimits(string usageText)
    {
        var rows = new List<LimitRow>();

        foreach (Match m in LimitLine().Matches(usageText))
        {
            if (!int.TryParse(m.Groups[2].Value, out var pct)) continue;

            var label = m.Groups[1].Value.Trim();
            var resets = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;

            rows.Add(LimitRow.Percentage(label, pct, resets, window: WindowFor(label)));
        }

        return rows;
    }

    /// <summary>
    /// How long a window runs. `/usage` never prints this, but it names the window, and the two
    /// Claude has are fixed: a session is five hours, a week is seven days. A row named anything
    /// else gets no length rather than a plausible one — the length is the denominator of the
    /// pace figure, and a guessed denominator produces a confident wrong answer.
    /// </summary>
    public static TimeSpan? WindowFor(string label) =>
        label.Contains("session", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(5)
        : label.Contains("week", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(7)
        : null;

    /// <summary>Locates the Claude CLI. Falls back to the bare name and lets the OS search PATH.</summary>
    public static string ResolveClaudePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return ProcessRunner.FindOnPath("claude") ?? "claude";
    }

    private static T? TryDeserialize<T>(string stdout, JsonTypeInfo<T> typeInfo) where T : class
    {
        // The CLI can emit warnings before the JSON, so start at the first brace.
        var start = stdout.IndexOf('{');
        if (start < 0) return null;

        try
        {
            return JsonSerializer.Deserialize(stdout[start..], typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(string what, ProcessResult r)
    {
        var detail = !string.IsNullOrWhiteSpace(r.StdErr) ? r.StdErr : r.StdOut;
        detail = detail.Trim();
        if (detail.Length > 200) detail = detail[..200] + "…";

        return string.IsNullOrEmpty(detail)
            ? $"{what} failed (exit {r.ExitCode})"
            : $"{what} failed (exit {r.ExitCode}): {detail}";
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
