using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>
/// Runs the Claude Code CLI to read subscription limits. No credentials are read or held here —
/// the CLI owns auth and token refresh. `/usage` is handled locally by the CLI: zero turns,
/// zero inference cost.
/// </summary>
public static partial class UsageProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Matches every limit line /usage prints, so new limit rows are picked up without a code change:
    //   "Current session: 0% used · resets Aug 15, 2:59am (Europe/Rome)"
    //   "Current week (all models): 93% used · resets Aug 15, 11:59am (Europe/Rome)"
    [GeneratedRegex(@"^(Current (?:session|week[^:]*)):\s*(\d+)%\s*used(?:\s*[·-]\s*resets\s+(.+?))?\s*$",
        RegexOptions.Multiline)]
    private static partial Regex LimitLine();

    /// <param name="knownAuth">
    /// A previously confirmed sign-in for this profile. Identity does not change between polls,
    /// so re-running `auth status` every time only costs another process launch. The caller
    /// drops its cached value whenever a probe fails, which is when it could be stale.
    /// </param>
    public static async Task<AccountStatus> ProbeAsync(
        AccountConfig account, string claudePath, AuthStatus? knownAuth, CancellationToken ct)
    {
        var status = new AccountStatus { Account = account, UpdatedAt = DateTime.Now };

        try
        {
            var authStatus = knownAuth;

            if (authStatus is null)
            {
                var auth = await RunAsync(claudePath, "auth status --json", account.ConfigDir, ct);

                // Exit code is non-zero when signed out, but the JSON body is still valid —
                // parse first.
                authStatus = TryDeserialize(auth.StdOut, JsonContext.Default.AuthStatus);
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

            var usage = await RunAsync(claudePath, "-p \"/usage\" --output-format json", account.ConfigDir, ct);

            var cli = TryDeserialize(usage.StdOut, JsonContext.Default.CliResult);
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
            return status;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status.Error = $"timed out after {Timeout.TotalSeconds:0}s";
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

            var resets = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            rows.Add(new LimitRow(m.Groups[1].Value.Trim(), pct, resets));
        }

        return rows;
    }

    /// <summary>Locates claude.exe. Falls back to bare "claude" and lets CreateProcess search PATH.</summary>
    public static string ResolveClaudePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry — skip it.
                }
            }
        }

        return "claude";
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

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static async Task<ProcessResult> RunAsync(
        string exe, string args, string? configDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Neutral cwd: avoids the per-directory trust prompt and any project-local settings.
            WorkingDirectory = Path.GetTempPath(),
        };

        if (!string.IsNullOrWhiteSpace(configDir))
            psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

        using var proc = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already gone.
        }
    }
}
