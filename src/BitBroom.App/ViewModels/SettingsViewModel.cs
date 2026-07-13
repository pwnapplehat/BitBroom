using BitBroom.App.Mvvm;
using BitBroom.Core.Settings;

namespace BitBroom.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;

    public SettingsViewModel(AppSettings settings, Action<string> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;

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
    }

    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand ResetCountersCommand { get; }

    /// <summary>Invoked when a setting that other tabs mirror (e.g. SimulateOnly) changes.</summary>
    public Action? OnSettingsChanged { get; init; }

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
}
