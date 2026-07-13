using System.Text;
using BitBroom.App.Mvvm;
using BitBroom.Core.Special;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

/// <summary>One tool as shown on the Tools page: what it is, what it does, how it runs.</summary>
public sealed record ToolInfo(
    string Title,
    string Description,
    string Symbol,
    bool RequiresAdmin,
    AsyncRelayCommand Command);

/// <summary>A titled group of related tools.</summary>
public sealed record ToolGroup(string Header, IReadOnlyList<ToolInfo> Tools);

public sealed class ToolsViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private readonly StringBuilder _console = new();
    private bool _isBusy;
    private string _consoleText = "Output of tools appears here.\n";

    public AsyncRelayCommand FlushDnsCommand { get; }
    public AsyncRelayCommand RestartExplorerCommand { get; }
    public AsyncRelayCommand AnalyzeWinSxSCommand { get; }
    public AsyncRelayCommand CleanWinSxSCommand { get; }
    public AsyncRelayCommand DisableHibernationCommand { get; }
    public AsyncRelayCommand ReduceHibernationCommand { get; }
    public AsyncRelayCommand EnableHibernationCommand { get; }
    public AsyncRelayCommand ListShadowStorageCommand { get; }
    public AsyncRelayCommand CompactVirtualDisksCommand { get; }
    public AsyncRelayCommand FreeUpOneDriveCommand { get; }
    public AsyncRelayCommand RemoveOldDriversCommand { get; }

    /// <summary>Everything the page renders: grouped tools with descriptions and admin flags.</summary>
    public IReadOnlyList<ToolGroup> Groups { get; }

    public ToolsViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;

        FlushDnsCommand = Make("Flush DNS cache", ct => SystemTools.FlushDnsAsync(AppendLine, ct), requiresAdmin: false);
        RestartExplorerCommand = Make("Restart Explorer", ct => SystemTools.RestartExplorerAsync(AppendLine, ct), requiresAdmin: false,
            confirm: "Restart Windows Explorer now? Open Explorer windows will close (apps keep running). This releases thumbnail cache locks.");
        AnalyzeWinSxSCommand = Make("Analyze component store", ct => SystemTools.AnalyzeComponentStoreAsync(AppendLine, ct), requiresAdmin: true);
        CleanWinSxSCommand = Make("Component store cleanup", ct => SystemTools.ComponentStoreCleanupAsync(AppendLine, ct), requiresAdmin: true,
            confirm: "Run DISM /StartComponentCleanup? This is Microsoft's supported WinSxS cleanup and can take 10–30 minutes. " +
                     "BitBroom intentionally does NOT use /ResetBase (which would permanently prevent uninstalling current updates).");
        DisableHibernationCommand = Make("Disable hibernation", ct => SystemTools.DisableHibernationAsync(AppendLine, ct), requiresAdmin: true,
            confirm: "Disable hibernation? This deletes hiberfil.sys (roughly 40% of your RAM in size) and also turns off Fast Startup. " +
                     "Re-enable any time with the Enable button.");
        ReduceHibernationCommand = Make("Reduce hiberfil (keep Fast Startup)", ct => SystemTools.ReduceHibernationFileAsync(AppendLine, ct), requiresAdmin: true,
            confirm: "Shrink hiberfil.sys to ~20% of RAM? Fast Startup keeps working; the full Hibernate option disappears from the power menu.");
        EnableHibernationCommand = Make("Enable hibernation", ct => SystemTools.EnableHibernationAsync(AppendLine, ct), requiresAdmin: true);
        ListShadowStorageCommand = Make("Show System Restore usage", ct => SystemTools.ListShadowStorageAsync(AppendLine, ct), requiresAdmin: true);
        CompactVirtualDisksCommand = Make("Compact WSL / Docker disks", ct => SystemTools.CompactVirtualDisksAsync(AppendLine, ct), requiresAdmin: true,
            confirm: "Compact your WSL/Docker virtual disks to reclaim empty space?\n\nThis only removes already-free blocks — no container, image, or file is lost. " +
                     "WSL will be shut down first (please quit Docker Desktop beforehand). It can take a few minutes per disk.");
        FreeUpOneDriveCommand = Make("Free up OneDrive space", ct => SystemTools.FreeUpOneDriveSpaceAsync(AppendLine, ct), requiresAdmin: false,
            confirm: "Make your OneDrive files online-only to reclaim local space?\n\nNothing is deleted — the cloud copy stays, and each file re-downloads automatically when you next open it. " +
                     "This is the same as right-clicking a folder and choosing 'Free up space'.");
        RemoveOldDriversCommand = Make("Remove old drivers", ct => SystemTools.RemoveOldDriversAsync(AppendLine, ct), requiresAdmin: true,
            confirm: "Remove superseded driver versions from the DriverStore?\n\nBitBroom keeps the NEWEST version of every driver and removes only older duplicates. " +
                     "Windows refuses to remove any driver still in use, so active hardware is safe. This can free several GB. Creating a restore point first is recommended.");

        Groups =
        [
            new ToolGroup("Storage & disks",
            [
                new ToolInfo("Compact WSL / Docker disks",
                    "WSL and Docker .vhdx virtual disks grow but never shrink on their own. Trims each distro, shuts WSL down, then compacts every disk — your files, images and containers are untouched. Quit Docker Desktop first.",
                    "Box24", true, CompactVirtualDisksCommand),
                new ToolInfo("Free up OneDrive space",
                    "Makes synced OneDrive files online-only so they stop using local disk. Nothing is deleted — the cloud copy stays and files re-download when you open them.",
                    "Cloud24", false, FreeUpOneDriveCommand),
                new ToolInfo("Remove old drivers",
                    "Windows keeps every driver version you have ever installed. Removes superseded versions via pnputil, always keeping the newest of each driver family; anything still in use is refused by Windows itself.",
                    "DeveloperBoard24", true, RemoveOldDriversCommand),
                new ToolInfo("Analyze component store",
                    "Asks DISM how large the WinSxS component store really is and how much of it is reclaimable. Read-only report — changes nothing.",
                    "DocumentSearch24", true, AnalyzeWinSxSCommand),
                new ToolInfo("Component store cleanup",
                    "Runs DISM StartComponentCleanup — Microsoft's supported WinSxS cleanup. Can free several GB after big updates; typically takes 10–30 minutes.",
                    "Broom24", true, CleanWinSxSCommand),
                new ToolInfo("System Restore usage",
                    "Shows how much disk space restore points are using on each drive. Read-only report — manage the space itself in System Protection.",
                    "History24", true, ListShadowStorageCommand),
            ]),
            new ToolGroup("Hibernation & power",
            [
                new ToolInfo("Disable hibernation",
                    "Deletes hiberfil.sys (roughly 40% of your RAM in size) and turns off Fast Startup. Frees the space instantly; re-enable any time.",
                    "WeatherMoon24", true, DisableHibernationCommand),
                new ToolInfo("Shrink hiberfil (keep Fast Startup)",
                    "Caps hiberfil.sys at ~20% of RAM. Fast Startup keeps working; only the full Hibernate option disappears from the power menu.",
                    "ArrowMinimize24", true, ReduceHibernationCommand),
                new ToolInfo("Enable hibernation",
                    "Restores full hibernation and Fast Startup (recreates hiberfil.sys at its default size).",
                    "Flash24", true, EnableHibernationCommand),
            ]),
            new ToolGroup("Quick fixes",
            [
                new ToolInfo("Flush DNS cache",
                    "Clears stale name-resolution entries — the classic fix when a site loads on your phone but not on this PC. Takes a second.",
                    "Globe24", false, FlushDnsCommand),
                new ToolInfo("Restart Explorer",
                    "Restarts the desktop shell to release thumbnail-cache locks and un-stick taskbar or File Explorer glitches. Open Explorer windows close; apps keep running.",
                    "ArrowClockwise24", false, RestartExplorerCommand),
            ]),
        ];
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                foreach (AsyncRelayCommand command in new[]
                {
                    FlushDnsCommand, RestartExplorerCommand, AnalyzeWinSxSCommand, CleanWinSxSCommand,
                    DisableHibernationCommand, ReduceHibernationCommand, EnableHibernationCommand, ListShadowStorageCommand,
                    CompactVirtualDisksCommand, FreeUpOneDriveCommand, RemoveOldDriversCommand,
                })
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string ConsoleText
    {
        get => _consoleText;
        private set => SetProperty(ref _consoleText, value);
    }

    private AsyncRelayCommand Make(
        string title,
        Func<CancellationToken, Task<ProcessResult>> action,
        bool requiresAdmin,
        string? confirm = null)
    {
        return new AsyncRelayCommand(async () =>
        {
            if (requiresAdmin && !ElevationInfo.IsElevated)
            {
                AppendLine($"'{title}' needs administrator rights — use “Restart as administrator” in the banner.");
                return;
            }

            if (confirm is not null)
            {
                var result = System.Windows.MessageBox.Show(confirm, $"BitBroom — {title}",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (result != System.Windows.MessageBoxResult.Yes)
                {
                    return;
                }
            }

            IsBusy = true;
            AppendLine($"── {title} ──");
            _setStatus($"{title}…");
            try
            {
                ProcessResult processResult = await action(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(processResult.Error))
                {
                    AppendLine(processResult.Error.Trim());
                }

                AppendLine(processResult.Success ? "Done." : $"Exited with code {processResult.ExitCode}.");
                _setStatus(processResult.Success ? $"{title}: done" : $"{title}: failed ({processResult.ExitCode})");
            }
            finally
            {
                IsBusy = false;
            }
        }, () => !_isBusy);
    }

    private void AppendLine(string line)
    {
        // Snapshot inside the lock: ToString() must not run while a worker thread mutates
        // the builder (StringBuilder is not thread-safe).
        string snapshot;
        lock (_console)
        {
            _console.AppendLine(line);
            if (_console.Length > 200_000)
            {
                _console.Remove(0, _console.Length - 150_000);
            }

            snapshot = _console.ToString();
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ConsoleText = snapshot);
    }
}
