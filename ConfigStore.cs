using System.Text.Json;

namespace ClaudeUsageWidget;

public static class ConfigStore
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageWidget");

    public static string FilePath => Path.Combine(Directory, "config.json");

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

        var config = JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonContext.Default.AppConfig)
                     ?? AppConfig.Default();

        if (config.Accounts.Count == 0)
            config.Accounts = AppConfig.Default().Accounts;

        if (config.PollMinutes < 1)
            config.PollMinutes = 5;

        return config;
    }

    public static void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonContext.Default.AppConfig));
    }
}
