using System.Collections.ObjectModel;
using System.IO;
using BitBroom.App.Mvvm;
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
    public CleanViewModel Clean { get; }
    public AnalyzerViewModel Analyzer { get; }
    public HogsViewModel Hogs { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel SettingsVm { get; }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand NavigateToCleanCommand { get; }

    /// <summary>Raised when a view model wants the shell to switch tabs (index into the nav order).</summary>
    public event Action<int>? NavigateRequested;

    public MainViewModel()
    {
        _settings = AppSettings.Load();

        Clean = new CleanViewModel(_settings, SetStatus) { OnCleanCompleted = RefreshDrives };
        Analyzer = new AnalyzerViewModel(SetStatus);
        Hogs = new HogsViewModel(SetStatus);
        Tools = new ToolsViewModel(SetStatus);
        SettingsVm = new SettingsViewModel(_settings, SetStatus) { OnSettingsChanged = () => Clean.NotifySettingsChanged() };

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

        RefreshDrives();
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
