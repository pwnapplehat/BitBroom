using System.Collections.ObjectModel;
using System.IO;
using BitBroom.App.Mvvm;
using BitBroom.App.Services;
using BitBroom.Core.Settings;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

public sealed class DriveViewModel
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }

    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
    public string UsageText => $"{ByteFormatter.Format(FreeBytes)} free of {ByteFormatter.Format(TotalBytes)}";
    public bool IsLow => TotalBytes > 0 && FreeBytes < TotalBytes / 10;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private string _statusText = "Ready";
    private UpdateInfo? _availableUpdate;
    private bool _updateBannerVisible;
    private string _updateBannerText = string.Empty;
    private bool _updateInProgress;
    public CleanViewModel Clean { get; }
    public AnalyzerViewModel Analyzer { get; }
    public DupesViewModel Dupes { get; }
    public HogsViewModel Hogs { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel SettingsVm { get; }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand NavigateToCleanCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand ViewUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }

    /// <summary>Raised when a view model wants the shell to switch tabs (index into the nav order).</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>Whether the splash intro should play its sweep sound (Settings toggle).</summary>
    public bool PlayStartupSound => _settings.PlayStartupSound;

    public MainViewModel()
    {
        _settings = AppSettings.Load();

        Clean = new CleanViewModel(_settings, SetStatus) { OnCleanCompleted = RefreshDrives };
        Analyzer = new AnalyzerViewModel(SetStatus);
        Dupes = new DupesViewModel(_settings, SetStatus);
        Hogs = new HogsViewModel(SetStatus);
        Tools = new ToolsViewModel(SetStatus);
        SettingsVm = new SettingsViewModel(_settings, SetStatus)
        {
            OnSettingsChanged = () => Clean.NotifySettingsChanged(),
            CheckForUpdatesNow = () => CheckForUpdatesAsync(manual: true),
        };

        RestartAsAdminCommand = new RelayCommand(() =>
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine process path");
            if (ElevationInfo.RelaunchAsAdministrator(exe))
            {
                System.Windows.Application.Current.Shutdown();
            }
        });

        OpenLogsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.LogsDirectory) { UseShellExecute = true });
        });

        NavigateToCleanCommand = new RelayCommand(() => NavigateRequested?.Invoke(1));

        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => !_updateInProgress);
        ViewUpdateCommand = new RelayCommand(() =>
        {
            if (_availableUpdate is { } update)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.ReleasePageUrl) { UseShellExecute = true });
            }
        });
        DismissUpdateCommand = new RelayCommand(() => UpdateBannerVisible = false);

        RefreshDrives();

        if (_settings.CheckForUpdatesAtStartup)
        {
            // Fire-and-forget: a failed or slow check must never affect startup.
            _ = CheckForUpdatesAsync(manual: false);
        }
    }

    // -------------------------------------------------------------------------
    // Updates
    // -------------------------------------------------------------------------

    public bool UpdateBannerVisible
    {
        get => _updateBannerVisible;
        private set => SetProperty(ref _updateBannerVisible, value);
    }

    public string UpdateBannerText
    {
        get => _updateBannerText;
        private set => SetProperty(ref _updateBannerText, value);
    }

    /// <summary>True when the release ships a checksummed installer we can auto-install.</summary>
    public bool UpdateCanAutoInstall => _availableUpdate is { InstallerUrl.Length: > 0, ChecksumsUrl.Length: > 0 };

    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            if (manual)
            {
                SetStatus("Checking for updates…");
            }

            UpdateInfo? update = await UpdateService.CheckAsync().ConfigureAwait(false);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (update is null)
                {
                    UpdateBannerVisible = false;
                    if (manual)
                    {
                        SetStatus($"You're up to date (v{UpdateService.CurrentVersion.ToString(3)} is the latest release).");
                    }

                    return;
                }

                _availableUpdate = update;
                UpdateBannerText = $"BitBroom {update.TagName} is available (you have v{UpdateService.CurrentVersion.ToString(3)}).";
                OnPropertyChanged(nameof(UpdateCanAutoInstall));
                UpdateBannerVisible = true;
                if (manual)
                {
                    SetStatus($"Update available: {update.TagName}");
                }
            });
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, GitHub down — all fine. Only a manual check reports it.
            if (manual)
            {
                SetStatus($"Update check failed: {ex.Message}");
            }
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update || !UpdateCanAutoInstall)
        {
            return;
        }

        _updateInProgress = true;
        InstallUpdateCommand.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<double>(fraction =>
                UpdateBannerText = $"Downloading {update.TagName}… {fraction:P0}");

            string installer = await UpdateService.DownloadVerifiedInstallerAsync(update, progress);

            UpdateBannerText = "Verified. Launching the installer — it will show progress and restart BitBroom…";

            System.Diagnostics.Process? process = UpdateService.LaunchInstaller(installer);
            if (process is null)
            {
                throw new InvalidOperationException("The installer could not be started.");
            }

            // The installer's own progress window now takes over; the app must exit so it
            // can replace the locked binaries (AppMutex makes it wait for us), and the
            // installer relaunches BitBroom when it finishes.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _updateInProgress = false;
            InstallUpdateCommand.RaiseCanExecuteChanged();
            UpdateBannerText = $"Update failed: {ex.Message} — you can still install it manually from the release page (What's new).";
            SetStatus("Update failed — nothing was changed, your current version is intact.");
        }
    }

    public bool IsElevated => ElevationInfo.IsElevated;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LifetimeFreedText => ByteFormatter.Format(_settings.LifetimeBytesFreed);

    public string LifetimeItemsText => _settings.LifetimeItemsDeleted.ToString("N0");

    public string LastCleanText => _settings.LastCleanUtc is { } utc
        ? utc.ToLocalTime().ToString("d MMM yyyy, HH:mm")
        : "never";

    public string VersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    private void SetStatus(string text)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = text;
            OnPropertyChanged(nameof(LifetimeFreedText));
            OnPropertyChanged(nameof(LifetimeItemsText));
            OnPropertyChanged(nameof(LastCleanText));
        });
    }

    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                Drives.Add(new DriveViewModel
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                });
            }
            catch (Exception)
            {
                // Unready/failing drives are skipped.
            }
        }
    }
}
