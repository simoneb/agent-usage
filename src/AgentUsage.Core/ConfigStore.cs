using System.Text.Json;

namespace AgentUsage;

public static class ConfigStore
{
    /// <summary>Overrides the location entirely. Set by the CLI's --config flag.</summary>
    public const string PathVariable = "AGENT_USAGE_CONFIG";

    /// <summary>
    /// One directory name on every platform, under whatever that platform calls its application
    /// data: %APPDATA% on Windows, ~/Library/Application Support on macOS, XDG_CONFIG_HOME on
    /// Linux. The widget and the CLI read the same file, so naming it after either of them would
    /// be wrong on the other.
    /// </summary>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } custom)
                return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(custom))
                       ?? System.IO.Directory.GetCurrentDirectory();

            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return System.IO.Path.Combine(root, "agent-usage");
        }
    }

    public static string FilePath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } custom
            ? System.IO.Path.GetFullPath(custom)
            : System.IO.Path.Combine(Directory, "config.json");

    /// <summary>Loads config, writing a default file on first run.</summary>
    public static AppConfig Load()
    {
        System.IO.Directory.CreateDirectory(Directory);

        if (!File.Exists(FilePath))
        {
            var fresh = AppConfig.Default();
            Save(fresh);
            return fresh;
        }

        var config = JsonSerializer.Deserialize(File.ReadAllText(FilePath), CoreJson.Default.AppConfig)
                     ?? AppConfig.Default();

        // Write the tidied version back so a retired key stops travelling with the file.
        if (config.Normalise())
            Save(config);

        return config;
    }

    public static void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, CoreJson.Default.AppConfig));
    }
}
