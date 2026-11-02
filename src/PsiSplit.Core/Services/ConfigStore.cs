using System.Text.Encodings.Web;
using System.Text.Json;
using PsiSplit.Core.Models;

namespace PsiSplit.Core.Services;

/// <summary>
/// Loads/saves %APPDATA%\PsiSplit\config.json. Writes atomically and keeps a
/// copy of a broken config instead of silently discarding it.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PsiSplit");

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string RootDirectory { get; }
    public string ConfigPath => Path.Combine(RootDirectory, "config.json");
    public string LogDirectory => Path.Combine(RootDirectory, "logs");
    public string ProxyBackupPath => Path.Combine(RootDirectory, "proxy-backup.json");

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return CreateDefault();
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefault();
            config.Normalize();
            return config;
        }
        catch
        {
            try
            {
                File.Copy(ConfigPath, ConfigPath + ".broken", overwrite: true);
            }
            catch
            {
                // ignore
            }

            return CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        config.Normalize();

        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, ConfigPath, overwrite: true);
    }

    public AppConfig CreateDefault()
    {
        return new AppConfig
        {
            // ChatGPT loads its UI and auth flow from several OpenAI domains,
            // so the whole set must go through the same filtered proxy.
            Sites = new List<SiteRule>
            {
                new() { Pattern = "chatgpt.com", Enabled = true },
                new() { Pattern = "openai.com", Enabled = true },
                new() { Pattern = "oaistatic.com", Enabled = true },
                new() { Pattern = "oaiusercontent.com", Enabled = true }
            }
        };
    }
}
