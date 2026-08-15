using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static ClaudeUsageWidget.Native;

namespace ClaudeUsageWidget;

/// <summary>
/// Checks whether a newer release exists. One unauthenticated GET to the GitHub API, at most
/// once a day, and never anything but a read — nothing about this machine goes anywhere.
/// </summary>
public static class Updates
{
    private const string ApiHost = "api.github.com";
    private const string ApiPath = "/repos/simoneb/claude-usage-widget/releases/latest";

    public const string ReleasesPage =
        "https://github.com/simoneb/claude-usage-widget/releases/latest";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Read from the binary's own version resource rather than assembly metadata, so it works
    /// the same under NativeAOT with no reflection involved.
    /// </summary>
    public static Version? CurrentVersion()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;

            var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion;

            return Version.TryParse(raw, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);

    /// <summary>
    /// Compares on major.minor.patch only. A tag parses to three components and a binary's
    /// version resource to four, and Version treats an absent revision as -1 — so comparing
    /// them directly would report 0.5.0 as older than the 0.5.0.0 already installed.
    /// </summary>
    public static bool IsNewer(string tag, Version? current)
    {
        if (current is null || !TryParseTag(tag, out var candidate)) return false;

        return Truncate(candidate) > Truncate(current);
    }

    private static Version Truncate(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>The newest published tag, or null if it could not be established.</summary>
    public static Task<string?> LatestTagAsync(CancellationToken ct) =>
        Task.Run(() => LatestTag(ct), ct);

    private static string? LatestTag(CancellationToken ct)
    {
        try
        {
            var body = Get(ApiHost, ApiPath, ct);
            if (body is null) return null;

            var release = JsonSerializer.Deserialize(body, JsonContext.Default.ReleaseInfo);

            return string.IsNullOrWhiteSpace(release?.TagName) ? null : release.TagName;
        }
        catch
        {
            // Offline, rate-limited, DNS-blocked, or a body that is not the JSON expected: all
            // the same answer, which is that we do not know. A widget that nags about its own
            // update check would be worse than a silent one.
            return null;
        }
    }

    private static string? Get(string host, string path, CancellationToken ct)
    {
        var agent = $"ClaudeUsageWidget/{CurrentVersion()?.ToString() ?? "0"}";

        var session = WinHttpOpen(agent, WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
        if (session == IntPtr.Zero) return null;

        try
        {
            WinHttpSetTimeouts(session, 5000, 5000, 5000, 10000);

            var connect = WinHttpConnect(session, host, INTERNET_DEFAULT_HTTPS_PORT, 0);
            if (connect == IntPtr.Zero) return null;

            try
            {
                var request = WinHttpOpenRequest(
                    connect, "GET", path, null, null, IntPtr.Zero, WINHTTP_FLAG_SECURE);

                if (request == IntPtr.Zero) return null;

                try
                {
                    const string headers = "Accept: application/vnd.github+json\r\n";

                    if (!WinHttpSendRequest(request, headers, (uint)headers.Length,
                            IntPtr.Zero, 0, 0, IntPtr.Zero))
                        return null;

                    if (!WinHttpReceiveResponse(request, IntPtr.Zero)) return null;

                    return ReadBody(request, ct);
                }
                finally
                {
                    WinHttpCloseHandle(request);
                }
            }
            finally
            {
                WinHttpCloseHandle(connect);
            }
        }
        finally
        {
            WinHttpCloseHandle(session);
        }
    }

    private static string? ReadBody(IntPtr request, CancellationToken ct)
    {
        // The release payload is a few KB. The cap is only here so a wrong endpoint or a hijacked
        // response cannot make this allocate without bound.
        const int limit = 512 * 1024;

        var body = new MemoryStream();
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            if (!WinHttpQueryDataAvailable(request, out var available)) return null;
            if (available == 0) break;

            var want = (uint)Math.Min(available, (uint)buffer.Length);
            if (!WinHttpReadData(request, buffer, want, out var read)) return null;
            if (read == 0) break;

            body.Write(buffer, 0, (int)read);
            if (body.Length > limit) return null;
        }

        return ct.IsCancellationRequested ? null : Encoding.UTF8.GetString(body.ToArray());
    }
}
