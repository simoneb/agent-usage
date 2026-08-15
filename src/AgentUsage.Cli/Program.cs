using AgentUsage;

namespace AgentUsage.Cli;

/// <summary>
/// The portable half of the project: reads the same accounts the Windows widget reads and prints
/// them, so a macOS menu bar or a Linux status bar can render usage without any of the Win32 code
/// existing. Text by default because a human ran it; --json when something else did.
/// </summary>
internal static class Program
{
    private const string Usage = """
        agent-usage — Claude Code and Codex subscription usage

        USAGE
          agent-usage [options]

        OPTIONS
          --json                 Machine-readable output (schema is stable; see README)
          --brief                One line, for a menu bar, tmux status or shell prompt
          --provider <id>        Only report accounts of this provider: claude, codex
          --config <path>        Use this config file instead of the default location
          --timeout <seconds>    Per-account probe timeout (default 30)
          --config-path          Print where config is read from and exit
          -h, --help             This text
          -v, --version          Version

        EXIT CODES
          0  ran to completion, whatever the accounts reported
          1  could not run at all — bad arguments, unreadable config

        An account that fails is reported as an error inside the output rather than as a failed
        run: a status bar polling this needs something to render either way.
        """;

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args, out var error);

        if (error is not null)
        {
            Console.Error.WriteLine($"agent-usage: {error}");
            Console.Error.WriteLine("Try 'agent-usage --help'.");
            return 1;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine(ThisAssembly.Version);
            return 0;
        }

        if (options.ConfigPath is { Length: > 0 } path)
            Environment.SetEnvironmentVariable(ConfigStore.PathVariable, path);

        if (options.ShowConfigPath)
        {
            Console.WriteLine(ConfigStore.FilePath);
            return 0;
        }

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"agent-usage: could not read {ConfigStore.FilePath}: {ex.Message}");
            return 1;
        }

        if (options.Provider is { Length: > 0 } wanted)
        {
            config.Accounts = config.Accounts
                .FindAll(a => string.Equals(
                    ProviderIds.Normalise(a.Provider), ProviderIds.Normalise(wanted),
                    StringComparison.OrdinalIgnoreCase));

            if (config.Accounts.Count == 0)
            {
                Console.Error.WriteLine($"agent-usage: no accounts configured for provider \"{wanted}\"");
                return 1;
            }
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        var statuses = await UsageService.ProbeAllAsync(
            config, knownAuth: null, cancel.Token, options.Timeout);
        var snapshot = Snapshot.From(statuses, DateTimeOffset.Now);

        Console.WriteLine(
            options.Json ? snapshot.ToJson()
            : options.Brief ? TextReport.RenderBrief(snapshot)
            : TextReport.Render(snapshot));

        return 0;
    }
}
