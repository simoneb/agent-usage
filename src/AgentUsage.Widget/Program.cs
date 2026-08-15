namespace AgentUsage.Widget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance only — two widgets polling the same accounts is pure waste.
        using var mutex = new Mutex(initiallyOwned: true, "AgentUsageWidget.SingleInstance", out var isNew);
        if (!isNew) return;

        using var widget = new Widget();
        widget.Run();
    }
}
