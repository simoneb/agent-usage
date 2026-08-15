using System.Diagnostics;
using System.Text;

namespace AgentUsage;

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs a provider's CLI and collects its output. Nothing here knows what it is running: the
/// provider decides the arguments and reads the result.
/// </summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ProcessResult> RunAsync(
        string exe,
        string args,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct)
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

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var proc = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Finds an executable on PATH. Windows needs the extensions spelled out because a CLI
    /// installed by npm is a .cmd shim rather than an .exe; everywhere else the bare name is
    /// the whole story.
    /// </summary>
    public static string? FindOnPath(string command)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
            : new[] { command };

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
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

        return null;
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
