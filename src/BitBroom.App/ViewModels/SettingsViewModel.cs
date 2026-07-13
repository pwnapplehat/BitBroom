using System.Collections.ObjectModel;
using BitBroom.App.Mvvm;
using BitBroom.Core.Settings;
using BitBroom.Core.Special;

namespace BitBroom.App.ViewModels;

/// <summary>An entry in the Settings exclusions list.</summary>
public sealed record ExcludedPathViewModel(string Path);

/// <summary>An entry in the Settings custom-folders list.</summary>
public sealed class CustomFolderViewModel
{
    public required CustomCleanFolder Model { get; init; }
    public string Path => Model.Path;
    public string AgeText => Model.MinAgeHours switch
    {
        0 => "no age limit",
        < 48 => $"older than {Model.MinAgeHours} h",
        < 24 * 14 => $"older than {Model.MinAgeHours / 24} days",
        _ => $"older than {Model.MinAgeHours / 24 / 7} weeks",
    };
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
    private string? _scheduleStatus;
    private bool _scheduleBusy;

    public ObservableCollection<ExcludedPathViewModel> ExcludedPaths { get; } = [];
    public ObservableCollection<CustomFolderViewModel> CustomFolders { get; } = [];

    public SettingsViewModel(AppSettings settings, Action<string> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;

        foreach (string path in settings.ExcludedPaths)
        {
            ExcludedPaths.Add(new ExcludedPathViewModel(path));
        }

        foreach (CustomCleanFolder folder in settings.CustomCleanFolders)
        {
            CustomFolders.Add(new CustomFolderViewModel { Model = folder });
        }

        OpenLogsCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(AppSettings.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.LogsDirectory) { UseShellExecute = true });
        });

        ResetCountersCommand = new RelayCommand(() =>
        {
            _settings.LifetimeBytesFreed = 0;
            _settings.LifetimeItemsDeleted = 0;
            _settings.Save();
            _setStatus("Lifetime counters reset");
        });

        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesNow?.Invoke() ?? Task.CompletedTask);

        AddExclusionCommand = new RelayCommand(AddExclusion);
        RemoveExclusionCommand = new RelayCommand(parameter =>
        {
            if (parameter is ExcludedPathViewModel entry)
            {
                ExcludedPaths.Remove(entry);
                _settings.ExcludedPaths.RemoveAll(p => string.Equals(p, entry.Path, StringComparison.OrdinalIgnoreCase));
                _settings.Save();
                _setStatus($"Exclusion removed: {entry.Path}");
            }
        });

        AddCustomFolderCommand = new RelayCommand(AddCustomFolder);
        RemoveCustomFolderCommand = new RelayCommand(parameter =>
        {
            if (parameter is CustomFolderViewModel entry)
            {
                CustomFolders.Remove(entry);
                _settings.CustomCleanFolders.Remove(entry.Model);
                _settings.Save();
                OnSettingsChanged?.Invoke();
                _setStatus($"Custom folder removed: {entry.Path}");
            }
        });

        ApplyScheduleCommand = new AsyncRelayCommand(ApplyScheduleAsync, () => !_scheduleBusy);

        _ = RefreshScheduleStatusAsync();
    }

    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand ResetCountersCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand AddExclusionCommand { get; }
    public RelayCommand RemoveExclusionCommand { get; }
    public RelayCommand AddCustomFolderCommand { get; }
    public RelayCommand RemoveCustomFolderCommand { get; }
    public AsyncRelayCommand ApplyScheduleCommand { get; }

    /// <summary>Invoked when a setting that other tabs mirror (e.g. SimulateOnly) changes.</summary>
    public Action? OnSettingsChanged { get; init; }

    /// <summary>Wired by the shell to the update checker ("Check now" button).</summary>
    public Func<Task>? CheckForUpdatesNow { get; init; }

    // -------------------------------------------------------------------------
    // Exclusions & custom folders
    // -------------------------------------------------------------------------

    private void AddExclusion()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder BitBroom must never touch" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FolderName;
        if (_settings.ExcludedPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            _setStatus("That folder is already excluded.");
            return;
        }

        _settings.ExcludedPaths.Add(path);
        _settings.Save();
        ExcludedPaths.Add(new ExcludedPathViewModel(path));
        _setStatus($"Excluded from all scans and cleans: {path}");
    }

    private void AddCustomFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to clean of old files" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FolderName;
        if (_settings.CustomCleanFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            _setStatus("That folder is already in the list.");
            return;
        }

        // Validate the same way the category will: protected locations are refused up front.
        var probe = CustomFoldersCategory.Create([new CustomCleanFolder { Path = path, MinAgeHours = 0 }]);
        if (probe.Rules.Count == 0)
        {
            _setStatus("That folder cannot be used (drive roots and malformed paths are refused).");
            return;
        }

        var folder = new CustomCleanFolder { Path = path, MinAgeHours = 24 * 7 };
        _settings.CustomCleanFolders.Add(folder);
        _settings.Save();
        CustomFolders.Add(new CustomFolderViewModel { Model = folder });
        OnSettingsChanged?.Invoke();
        _setStatus($"Custom folder added (files older than 7 days): {path}. Protected locations are refused at scan time.");
    }

    // -------------------------------------------------------------------------
    // Scheduled cleaning
    // -------------------------------------------------------------------------

    public bool ScheduledCleaningEnabled
    {
        get => _settings.ScheduledCleaningEnabled;
        set
        {
            _settings.ScheduledCleaningEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            _ = ApplyScheduleAsync();
        }
    }

    public int ScheduleFrequencyIndex
    {
        get => _settings.ScheduleFrequency;
        set
        {
            _settings.ScheduleFrequency = Math.Clamp(value, 0, 2);
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWeekly));
        }
    }

    public bool IsWeekly => _settings.ScheduleFrequency == 1;

    public int ScheduleDayIndex
    {
        get => _settings.ScheduleDayOfWeek;
        set
        {
            _settings.ScheduleDayOfWeek = Math.Clamp(value, 0, 6);
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public int ScheduleHourIndex
    {
        get => _settings.ScheduleHour;
        set
        {
            _settings.ScheduleHour = Math.Clamp(value, 0, 23);
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string? ScheduleStatus
    {
        get => _scheduleStatus;
        private set => SetProperty(ref _scheduleStatus, value);
    }

    public bool SchedulingAvailable => ScheduledCleaning.FindCliPath() is not null;

    private async Task ApplyScheduleAsync()
    {
        _scheduleBusy = true;
        ApplyScheduleCommand.RaiseCanExecuteChanged();
        try
        {
            if (_settings.ScheduledCleaningEnabled)
            {
                (bool ok, string message) = await ScheduledCleaning.CreateOrUpdateAsync(
                    (ScheduleFrequency)_settings.ScheduleFrequency,
                    (DayOfWeek)_settings.ScheduleDayOfWeek,
                    _settings.ScheduleHour);
                _setStatus(message);
            }
            else
            {
                (bool ok, string message) = await ScheduledCleaning.RemoveAsync();
                _setStatus(message);
            }

            await RefreshScheduleStatusAsync();
        }
        finally
        {
            _scheduleBusy = false;
            ApplyScheduleCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RefreshScheduleStatusAsync()
    {
        try
        {
            ScheduleState state = await ScheduledCleaning.QueryAsync();
            ScheduleStatus = state.Exists
                ? $"Active — {ScheduledCleaning.Describe((ScheduleFrequency)_settings.ScheduleFrequency, (DayOfWeek)_settings.ScheduleDayOfWeek, _settings.ScheduleHour)}. {state.Summary}"
                : "No scheduled clean registered.";

            // Reconcile the toggle with reality (task deleted externally, etc.).
            if (_settings.ScheduledCleaningEnabled != state.Exists)
            {
                _settings.ScheduledCleaningEnabled = state.Exists;
                _settings.Save();
                OnPropertyChanged(nameof(ScheduledCleaningEnabled));
            }
        }
        catch (Exception)
        {
            ScheduleStatus = "Task Scheduler state unavailable.";
        }
    }

    public int MinAgeHours
    {
        get => _settings.MinAgeHours;
        set
        {
            _settings.MinAgeHours = Math.Clamp(value, 0, 24 * 365);
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinAgeSelectedIndex));
        }
    }

    /// <summary>0h / 24h / 48h / 7d / 30d choices for the ComboBox.</summary>
    public int MinAgeSelectedIndex
    {
        get => _settings.MinAgeHours switch
        {
            0 => 0,
            <= 24 => 1,
            <= 48 => 2,
            <= 168 => 3,
            _ => 4,
        };
        set
        {
            MinAgeHours = value switch
            {
                0 => 0,
                1 => 24,
                2 => 48,
                3 => 168,
                _ => 720,
            };
        }
    }

    public bool SimulateOnly
    {
        get => _settings.SimulateOnly;
        set
        {
            _settings.SimulateOnly = value;
            _settings.Save();
            OnPropertyChanged();
            OnSettingsChanged?.Invoke();
            _setStatus(value ? "Simulation mode ON — cleans only log, never delete" : "Simulation mode off");
        }
    }

    public bool ConfirmBeforeClean
    {
        get => _settings.ConfirmBeforeClean;
        set
        {
            _settings.ConfirmBeforeClean = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CreateRestorePointBeforeClean
    {
        get => _settings.CreateRestorePointBeforeClean;
        set
        {
            _settings.CreateRestorePointBeforeClean = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool PlayStartupSound
    {
        get => _settings.PlayStartupSound;
        set
        {
            _settings.PlayStartupSound = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CheckForUpdatesAtStartup
    {
        get => _settings.CheckForUpdatesAtStartup;
        set
        {
            _settings.CheckForUpdatesAtStartup = value;
            _settings.Save();
            OnPropertyChanged();
            _setStatus(value ? "Update check at startup enabled" : "Update check at startup disabled — BitBroom makes no network requests at all now");
        }
    }

    public bool CleanToRecycleBin
    {
        get => _settings.CleanToRecycleBin;
        set
        {
            _settings.CleanToRecycleBin = value;
            _settings.Save();
            OnPropertyChanged();
            _setStatus(value
                ? "Cleans now send files to the Recycle Bin — space is freed when you empty it"
                : "Cleans delete permanently again");
        }
    }
}
