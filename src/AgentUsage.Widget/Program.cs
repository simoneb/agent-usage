namespace AgentUsage.Widget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance only — two widgets polling the same accounts is pure waste.
        using var mutex = new Mutex(initiallyOwned: true, "AgentUsageWidget.SingleInstance", out var isNew);
        if (!isNew)
        {
            ShowRunningInstance();
            return;
        }

        using var widget = new Widget();
        widget.Run();
    }

    // Launching the app again — from the Start menu, usually — while it sits hidden in the tray
    // used to exit without a trace, which reads as "the app won't start". The person clicking
    // wants the panel, so ask the instance that owns the mutex to show it. This process was just
    // started by the user and holds the foreground right; passing it on lets the panel actually
    // come to the front instead of only flashing on the taskbar.
    private static void ShowRunningInstance()
    {
        var hwnd = Native.FindWindowW(Widget.ClassName, null);
        if (hwnd == IntPtr.Zero) return;

        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Native.PostMessageW(hwnd, Native.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }
}
