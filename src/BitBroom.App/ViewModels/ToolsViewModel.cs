using System.Text;
using BitBroom.App.Mvvm;
using BitBroom.Core.Special;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

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
