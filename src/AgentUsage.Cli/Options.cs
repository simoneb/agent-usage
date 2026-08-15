namespace AgentUsage.Cli;

internal sealed class Options
{
    public bool Json { get; private set; }
    public bool Brief { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool ShowConfigPath { get; private set; }
    public string? Provider { get; private set; }
    public string? ConfigPath { get; private set; }
    public TimeSpan Timeout { get; private set; } = ProcessRunner.DefaultTimeout;

    /// <summary>
    /// Hand-rolled rather than a parser package: the dependency would outweigh the binary this
    /// is trying to keep small, and there are six flags.
    /// </summary>
    public static Options Parse(string[] args, out string? error)
    {
        var options = new Options();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    options.Json = true;
                    break;

                case "--brief":
                    options.Brief = true;
                    break;

                case "-h" or "--help":
                    options.ShowHelp = true;
                    break;

                case "-v" or "--version":
                    options.ShowVersion = true;
                    break;

                case "--config-path":
                    options.ShowConfigPath = true;
                    break;

                case "--provider":
                    if (!TryValue(args, ref i, out var provider))
                    {
                        error = "--provider needs a value";
                        return options;
                    }

                    if (!ProviderIds.IsKnown(provider))
                    {
                        error = $"unknown provider \"{provider}\" — expected one of: " +
                                string.Join(", ", ProviderIds.All);
                        return options;
                    }

                    options.Provider = provider;
                    break;

                case "--config":
                    if (!TryValue(args, ref i, out var path))
                    {
                        error = "--config needs a value";
                        return options;
                    }

                    options.ConfigPath = path;
                    break;

                case "--timeout":
                    if (!TryValue(args, ref i, out var raw) ||
                        !double.TryParse(raw, out var seconds) || seconds <= 0)
                    {
                        error = "--timeout needs a positive number of seconds";
                        return options;
                    }

                    options.Timeout = TimeSpan.FromSeconds(seconds);
                    break;

                default:
                    error = $"unrecognised option \"{args[i]}\"";
                    return options;
            }
        }

        return options;
    }

    private static bool TryValue(string[] args, ref int i, out string value)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
        {
            value = string.Empty;
            return false;
        }

        value = args[++i];
        return true;
    }
}
