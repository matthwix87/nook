using System.Text.Json;
using System.Text.Json.Serialization;
using Nook.Core.Platform;

namespace Nook.Core;

/// <summary>Small per-machine preferences (not secrets). Stored as settings.json in the config dir.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("backend_override")] public string BackendOverride { get; set; } = "";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system"; // system | light | dark
    [JsonPropertyName("last_user")] public string LastUser { get; set; } = "";
    [JsonPropertyName("last_workspace")] public string LastWorkspace { get; set; } = "";

    public static AppSettings Load(string configDir)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(configDir, "settings.json"))) ?? new();
        }
        catch { return new(); }
    }

    public void Save(string configDir)
    {
        try
        {
            ConfigFiles.EnsureDir(configDir);
            ConfigFiles.WritePrivate(Path.Combine(configDir, "settings.json"),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are best effort */ }
    }
}
