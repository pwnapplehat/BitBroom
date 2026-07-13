using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitBroom.Core.Settings;

/// <summary>A user-chosen folder cleaned by the "custom-folders" category.</summary>
public sealed class CustomCleanFolder
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Only delete files older than this many hours (0 = no age limit).</summary>
    public int MinAgeHours { get; set; } = 24 * 7;
}

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

    /// <summary>
    /// Query the GitHub releases API once at startup for a newer version. This is the
    /// only network request BitBroom can make, and it can be turned off here.
    /// </summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    /// <summary>
    /// Send cleaned files to the Recycle Bin instead of deleting permanently. Off by
    /// default: space is only truly freed once the bin is emptied, and temp files can
    /// flood it — but it gives new users an undo window while they build trust.
    /// </summary>
    public bool CleanToRecycleBin { get; set; }

    /// <summary>Paths (with their subtrees) that scanning and cleaning must never touch.</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>User-defined folders cleaned as the "custom-folders" category.</summary>
    public List<CustomCleanFolder> CustomCleanFolders { get; set; } = [];

    /// <summary>Mirror of the scheduled-cleaning UI state (the Task Scheduler task is authoritative).</summary>
    public bool ScheduledCleaningEnabled { get; set; }

    /// <summary>0 = daily, 1 = weekly, 2 = monthly.</summary>
    public int ScheduleFrequency { get; set; } = 1;

    /// <summary>Day of week for weekly schedules (0 = Sunday … 6 = Saturday).</summary>
    public int ScheduleDayOfWeek { get; set; }

    /// <summary>Hour of day (0–23) the scheduled clean runs.</summary>
    public int ScheduleHour { get; set; } = 9;

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

        ExcludedPaths ??= [];
        ExcludedPaths.RemoveAll(string.IsNullOrWhiteSpace);

        CustomCleanFolders ??= [];
        CustomCleanFolders.RemoveAll(f => f is null || string.IsNullOrWhiteSpace(f.Path));
        foreach (CustomCleanFolder folder in CustomCleanFolders)
        {
            folder.MinAgeHours = Math.Clamp(folder.MinAgeHours, 0, 24 * 365);
        }

        ScheduleFrequency = Math.Clamp(ScheduleFrequency, 0, 2);
        ScheduleDayOfWeek = Math.Clamp(ScheduleDayOfWeek, 0, 6);
        ScheduleHour = Math.Clamp(ScheduleHour, 0, 23);
    }
}
