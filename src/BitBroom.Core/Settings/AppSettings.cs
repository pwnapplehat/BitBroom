using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitBroom.Core.Settings;

/// <summary>
/// Persisted application settings and lifetime counters.
/// Stored as JSON under %LocalAppData%\BitBroom\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Files younger than this many hours are never deleted by rule-based categories.
    /// Protects installers and apps that are actively using fresh temp files.
    /// </summary>
    public int MinAgeHours { get; set; } = 24;

    /// <summary>When true, cleaning logs what would be deleted without deleting anything.</summary>
    public bool SimulateOnly { get; set; }

    /// <summary>Attempt to create a System Restore point before cleaning system-area categories.</summary>
    public bool CreateRestorePointBeforeClean { get; set; }

    /// <summary>Ask for confirmation before every clean.</summary>
    public bool ConfirmBeforeClean { get; set; } = true;

    /// <summary>Play the short broom-sweep sound with the startup splash animation.</summary>
    public bool PlayStartupSound { get; set; } = true;

    /// <summary>Per-category enabled/disabled overrides chosen by the user (persisted between runs).</summary>
    public Dictionary<string, bool> CategorySelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long LifetimeBytesFreed { get; set; }
    public long LifetimeItemsDeleted { get; set; }
    public DateTime? LastCleanUtc { get; set; }

    [JsonIgnore]
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitBroom");

    [JsonIgnore]
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings must never prevent startup; fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception)
        {
            // Persisting settings is best-effort; never crash the app over it.
        }
    }

    private void Clamp()
    {
        if (MinAgeHours < 0)
        {
            MinAgeHours = 0;
        }

        if (MinAgeHours > 24 * 365)
        {
            MinAgeHours = 24 * 365;
        }
    }
}
