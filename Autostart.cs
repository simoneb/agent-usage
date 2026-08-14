using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>
/// Starting with Windows, via the per-user Run key — the same mechanism Slack and Docker
/// Desktop use here, so it needs no elevation and shows up in Task Manager's Startup apps
/// where a user can turn it off without knowing this app wrote it.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageWidget";

    /// <summary>The binary currently running, which is the one worth registering.</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// True only when the registered command points at this exe. A value left behind by a copy
    /// that has since moved reads as off, so clicking the item repairs it rather than leaving
    /// a checkmark on a path that no longer starts anything.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return key?.GetValue(ValueName) is string command &&
                   SamePath(command, ExePath);
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        var exe = ExePath;
        if (enabled && string.IsNullOrEmpty(exe)) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
                key.SetValue(ValueName, Quote(exe!), RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            // The menu re-reads the registry every time it opens, so a failure shows up as the
            // checkmark refusing to move. That is clearer than a dialog nobody asked for.
            return false;
        }
    }

    /// <summary>The shell splits an unquoted command at the first space, so paths are quoted.</summary>
    public static string Quote(string path) => path.StartsWith('"') ? path : $"\"{path}\"";

    public static bool SamePath(string command, string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;

        var stored = command.Trim().Trim('"');

        return string.Equals(stored, exe, StringComparison.OrdinalIgnoreCase);
    }
}
