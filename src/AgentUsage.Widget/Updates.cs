using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static AgentUsage.Widget.Native;

namespace AgentUsage.Widget;

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
}

/// <summary>The parts of the GitHub release payload this needs: what version, and which files.</summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset>? Assets { get; set; }
}

/// <summary>
/// The widget's own serialisation context. Separate from the core's: this type belongs to the
/// Windows update check, which is not something the portable half knows about.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReleaseInfo))]
internal partial class WidgetJson : JsonSerializerContext;

/// <summary>What an install attempt did, in enough detail to tell the user something true.</summary>
public enum InstallResult
{
    Installed,
    AlreadyCurrent,
    NoRelease,
    NoAsset,
    DownloadFailed,
    ChecksumMismatch,
    NotWritable,
}

/// <summary>
/// Checks whether a newer release exists, and — when asked — installs it. Everything here talks
/// to one host, reads only what GitHub publishes, and never sends anything about this machine.
///
/// The install is the rename dance every portable Windows app ends up writing: Windows will not
/// let a running .exe be overwritten, but it will happily let it be renamed out of the way. So
/// the new build lands beside the old one, the old one becomes .old, the new one takes its name,
/// and the next start sweeps up. Nothing is touched until the download's checksum matches the one
/// published with the release, so a truncated or substituted file replaces nothing.
/// </summary>
public static class Updates
{
    private const string ApiHost = "api.github.com";
    private const string ApiPath = "/repos/simoneb/agent-usage/releases/latest";

    private const string SumsAsset = "SHA256SUMS.txt";

    public const string ReleasesPage =
        "https://github.com/simoneb/agent-usage/releases/latest";

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
        Task.Run(() => LatestRelease(ct)?.TagName is { Length: > 0 } tag ? tag : null, ct);

    /// <summary>Downloads the newest release and swaps this binary for it. Off the UI thread.</summary>
    public static Task<InstallResult> InstallLatestAsync(CancellationToken ct) =>
        Task.Run(() => InstallLatest(ct), ct);

    /// <summary>
    /// The asset this machine can run. The widget ships one binary per architecture and they are
    /// not interchangeable, so an unrecognised architecture downloads nothing at all.
    /// </summary>
    public static string? AssetNameFor(Architecture arch) => arch switch
    {
        Architecture.X64 => "AgentUsageWidget-win-x64.exe",
        Architecture.Arm64 => "AgentUsageWidget-win-arm64.exe",
        _ => null,
    };

    /// <summary>
    /// The hash SHA256SUMS.txt publishes for one asset. The file is `sha256sum` output — hash,
    /// blank, name — and it lists every asset in the release, the CLI builds included.
    /// </summary>
    public static string? HashFor(string sums, string assetName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var trimmed = line.Trim();
            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;

            // The name half carries a leading '*' for a binary-mode entry on some tools.
            var name = trimmed[(space + 1)..].TrimStart(' ', '*');
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) continue;

            return trimmed[..space].ToLowerInvariant();
        }

        return null;
    }

    /// <summary>
    /// Only GitHub's own hosts. A release payload is not something to take a download URL from
    /// on trust: whatever comes back here is about to be run as this application.
    /// </summary>
    public static bool IsTrustedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Removes what a previous update left behind. The binary that was running at the time could
    /// not delete itself, so it renamed itself aside and left this for its successor.
    /// </summary>
    public static void CleanLeftovers()
    {
        Sweep();

        // The binary that renamed itself aside started this one and then quit, so at this exact
        // moment it is usually still exiting and Windows still has its file locked. One retry a
        // few seconds later is the difference between the leftover going now and going at some
        // unrelated start days from now.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Sweep();
        });
    }

    private static void Sweep()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        foreach (var suffix in new[] { ".old", ".new" })
        {
            try
            {
                if (File.Exists(exe + suffix)) File.Delete(exe + suffix);
            }
            catch
            {
                // Still locked, or not ours to delete. The next start gets it.
            }
        }
    }

    private static InstallResult InstallLatest(CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return InstallResult.NotWritable;

        var release = LatestRelease(ct);
        if (release?.TagName is not { Length: > 0 } tag) return InstallResult.NoRelease;
        if (!IsNewer(tag, CurrentVersion())) return InstallResult.AlreadyCurrent;

        var wanted = AssetNameFor(RuntimeInformation.ProcessArchitecture);
        if (wanted is null) return InstallResult.NoAsset;

        var asset = Find(release, wanted);
        var sumsAsset = Find(release, SumsAsset);

        if (asset?.Url is not { Length: > 0 } assetUrl ||
            sumsAsset?.Url is not { Length: > 0 } sumsUrl ||
            !IsTrustedDownload(assetUrl) || !IsTrustedDownload(sumsUrl))
            return InstallResult.NoAsset;

        var sums = GetText(sumsUrl, null, ct);
        if (sums is null || HashFor(sums, wanted) is not { Length: 64 } expected)
            return InstallResult.DownloadFailed;

        var staged = exe + ".new";

        try
        {
            if (!Download(assetUrl, staged, ct)) return InstallResult.DownloadFailed;

            if (!string.Equals(Sha256Of(staged), expected, StringComparison.OrdinalIgnoreCase))
                return InstallResult.ChecksumMismatch;

            return Swap(exe, staged);
        }
        catch
        {
            return InstallResult.DownloadFailed;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    /// <summary>
    /// Rename the running binary aside, move the new one into its place, and put the old one
    /// back if that second move fails — the one window in which this could leave no binary at
    /// the expected path at all.
    /// </summary>
    private static InstallResult Swap(string exe, string staged)
    {
        var retired = exe + ".old";

        try
        {
            if (File.Exists(retired)) File.Delete(retired);
            File.Move(exe, retired);
        }
        catch
        {
            // An installation directory this user cannot write to — Program Files, most likely.
            return InstallResult.NotWritable;
        }

        try
        {
            File.Move(staged, exe);
            return InstallResult.Installed;
        }
        catch
        {
            try { File.Move(retired, exe); } catch { }
            return InstallResult.NotWritable;
        }
    }

    private static ReleaseAsset? Find(ReleaseInfo release, string name) =>
        release.Assets?.Find(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ReleaseInfo? LatestRelease(CancellationToken ct)
    {
        try
        {
            const string headers = "Accept: application/vnd.github+json\r\n";

            var body = GetText(ApiHost, ApiPath, headers, ct);
            if (body is null) return null;

            return JsonSerializer.Deserialize(body, WidgetJson.Default.ReleaseInfo);
        }
        catch
        {
            // Offline, rate-limited, DNS-blocked, or a body that is not the JSON expected: all
            // the same answer, which is that we do not know. A widget that nags about its own
            // update check would be worse than a silent one.
            return null;
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? GetText(string url, string? headers, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        return GetText(uri.Host, uri.PathAndQuery, headers, ct);
    }

    private static string? GetText(string host, string path, string? headers, CancellationToken ct)
    {
        // A release payload is a few KB and a checksum list smaller still. The cap is only here
        // so a wrong endpoint or a hijacked response cannot make this allocate without bound.
        using var body = new MemoryStream();

        return Fetch(host, path, headers, body, 512 * 1024, ct)
            ? Encoding.UTF8.GetString(body.ToArray())
            : null;
    }

    private static bool Download(string url, string destination, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        // Generous next to a 3 MB binary, and still small enough that a redirect to something
        // else entirely cannot fill the disk.
        return Fetch(uri.Host, uri.PathAndQuery, null, file, 64 * 1024 * 1024, ct);
    }

    /// <remarks>
    /// Redirects are followed without asking: WinHTTP's default policy allows them for
    /// same-scheme hops. That is what carried this over the rename from claude-usage-widget —
    /// GitHub answers the old path with a 301, and binaries built before the rename follow it
    /// and keep seeing releases — and it is also how an asset URL reaches the storage host it
    /// actually lives on.
    /// </remarks>
    private static bool Fetch(
        string host, string path, string? headers, Stream sink, int limit, CancellationToken ct)
    {
        var agent = $"AgentUsageWidget/{CurrentVersion()?.ToString() ?? "0"}";

        var session = WinHttpOpen(agent, WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
        if (session == IntPtr.Zero) return false;

        try
        {
            // The download is the long one of these; a release binary over a slow link should
            // not be cut off at the same five seconds that suit an API call.
            WinHttpSetTimeouts(session, 5000, 5000, 10000, 60000);

            var connect = WinHttpConnect(session, host, INTERNET_DEFAULT_HTTPS_PORT, 0);
            if (connect == IntPtr.Zero) return false;

            try
            {
                var request = WinHttpOpenRequest(
                    connect, "GET", path, null, null, IntPtr.Zero, WINHTTP_FLAG_SECURE);

                if (request == IntPtr.Zero) return false;

                try
                {
                    if (!WinHttpSendRequest(request, headers, (uint)(headers?.Length ?? 0),
                            IntPtr.Zero, 0, 0, IntPtr.Zero))
                        return false;

                    if (!WinHttpReceiveResponse(request, IntPtr.Zero)) return false;
                    if (StatusCode(request) is not 200) return false;

                    return ReadBody(request, sink, limit, ct);
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

    /// <summary>
    /// Without this a 404 body would be parsed as if it were the thing asked for. Harmless for
    /// the JSON, which simply fails to deserialise — but a download has to fail loudly rather
    /// than write an error page to disk and check its hash.
    /// </summary>
    private static uint StatusCode(IntPtr request)
    {
        uint code = 0;
        var size = (uint)sizeof(uint);

        return WinHttpQueryHeaders(request,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, null,
            ref code, ref size, IntPtr.Zero)
            ? code
            : 0;
    }

    private static bool ReadBody(IntPtr request, Stream sink, int limit, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;

        while (!ct.IsCancellationRequested)
        {
            if (!WinHttpQueryDataAvailable(request, out var available)) return false;
            if (available == 0) break;

            var want = (uint)Math.Min(available, (uint)buffer.Length);
            if (!WinHttpReadData(request, buffer, want, out var read)) return false;
            if (read == 0) break;

            total += read;
            if (total > limit) return false;

            sink.Write(buffer, 0, (int)read);
        }

        if (ct.IsCancellationRequested) return false;

        sink.Flush();
        return total > 0;
    }
}
