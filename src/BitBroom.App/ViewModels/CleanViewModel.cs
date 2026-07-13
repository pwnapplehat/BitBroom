using System.Collections.ObjectModel;
using System.Windows.Data;
using BitBroom.App.Mvvm;
using BitBroom.Core.Catalog;
using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;
using BitBroom.Core.Special;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

public sealed class CategoryItemViewModel : ObservableObject
{
    private bool _isSelected;
    private long _sizeBytes = -1;
    private int _fileCount;
    private bool _notDetected;
    private bool _scanned;
    private string? _scanNote;

    public required CleanCategory Category { get; init; }

    public string Name => Category.Name;
    public string Description => Category.Description;
    public string GroupName => Category.Group switch
    {
        CategoryGroup.System => "System",
        CategoryGroup.Browsers => "Browsers",
        CategoryGroup.Applications => "Applications",
        CategoryGroup.GamingAndGpu => "Gaming & GPU",
        CategoryGroup.Development => "Development",
        _ => "Advanced",
    };

    public RiskLevel Risk => Category.Risk;
    public string RiskLabel => Category.Risk switch
    {
        RiskLevel.Safe => "SAFE",
        RiskLevel.Moderate => "MODERATE",
        _ => "ADVANCED",
    };

    public bool RequiresAdmin => Category.RequiresAdmin;
    public bool AdminLocked => Category.RequiresAdmin && !ElevationInfo.IsElevated;
    public string? Warning => Category.Warning;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public string SizeText => !_scanned
        ? ""
        : _notDetected
            ? AdminLocked ? "requires admin" : "not installed"
            : ByteFormatter.Format(Math.Max(0, _sizeBytes));

    public int FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value);
    }

    public bool NotDetected
    {
        get => _notDetected;
        set
        {
            if (SetProperty(ref _notDetected, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public bool Scanned
    {
        get => _scanned;
        set
        {
            if (SetProperty(ref _scanned, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public string? ScanNote
    {
        get => _scanNote;
        set => SetProperty(ref _scanNote, value);
    }
}

public sealed class CleanViewModel : ObservableObject
{
    private readonly CleaningEngine _engine = new();
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;

    private CancellationTokenSource? _cts;
    private Dictionary<string, CategoryScanResult>? _lastScan;

    private bool _isBusy;
    private bool _hasScanned;
    private string _progressText = string.Empty;
    private long _selectedBytes;
    private string? _resultSummary;
    private string? _resultLogPath;
    private bool _confirmVisible;
    private string _confirmText = string.Empty;

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];
    public ListCollectionView GroupedCategories { get; }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectDefaultsCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ConfirmCleanCommand { get; }
    public RelayCommand CancelConfirmCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    /// <summary>Invoked after a real (non-simulated) clean so the shell can refresh drive stats.</summary>
    public Action? OnCleanCompleted { get; init; }

    public CleanViewModel(AppSettings settings, Action<string> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;

        foreach (CleanCategory category in CategoryCatalog.Build())
        {
            var item = new CategoryItemViewModel { Category = category };
            item.IsSelected = settings.CategorySelections.TryGetValue(category.Id, out bool remembered)
                ? remembered && !item.AdminLocked
                : category.EnabledByDefault && !item.AdminLocked;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CategoryItemViewModel.IsSelected))
                {
                    OnSelectionChanged(item);
                }
            };
            Categories.Add(item);
        }

        GroupedCategories = new ListCollectionView(Categories);
        GroupedCategories.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CategoryItemViewModel.GroupName)));

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_isBusy);
        CleanCommand = new AsyncRelayCommand(RequestCleanAsync, () => !_isBusy && _hasScanned && SelectedBytes > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _isBusy);
        SelectDefaultsCommand = new RelayCommand(() => ApplySelection(c => c.Category.EnabledByDefault));
        SelectNoneCommand = new RelayCommand(() => ApplySelection(_ => false));
        ConfirmCleanCommand = new RelayCommand(() => { ConfirmVisible = false; _ = RunCleanGuardedAsync(); });
        CancelConfirmCommand = new RelayCommand(() => ConfirmVisible = false);
        OpenLogCommand = new RelayCommand(() =>
        {
            if (_resultLogPath is not null && System.IO.File.Exists(_resultLogPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_resultLogPath) { UseShellExecute = true });
            }
        });

        RecomputeSelectedBytes();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CleanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetProperty(ref _hasScanned, value))
            {
                CleanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public long SelectedBytes
    {
        get => _selectedBytes;
        private set
        {
            if (SetProperty(ref _selectedBytes, value))
            {
                OnPropertyChanged(nameof(SelectedBytesText));
                CleanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedBytesText => ByteFormatter.Format(SelectedBytes);

    public string? ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string? ResultLogPath
    {
        get => _resultLogPath;
        private set => SetProperty(ref _resultLogPath, value);
    }

    public bool ConfirmVisible
    {
        get => _confirmVisible;
        private set => SetProperty(ref _confirmVisible, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => SetProperty(ref _confirmText, value);
    }

    public bool SimulateOnly => _settings.SimulateOnly;

    /// <summary>Re-reads settings-derived state (called when the user changes Settings elsewhere).</summary>
    public void NotifySettingsChanged() => OnPropertyChanged(nameof(SimulateOnly));

    // -------------------------------------------------------------------------

    private void OnSelectionChanged(CategoryItemViewModel item)
    {
        _settings.CategorySelections[item.Category.Id] = item.IsSelected;
        _settings.Save();
        RecomputeSelectedBytes();
    }

    private void ApplySelection(Func<CategoryItemViewModel, bool> selector)
    {
        foreach (CategoryItemViewModel item in Categories)
        {
            item.IsSelected = selector(item) && !item.AdminLocked;
        }
    }

    private void RecomputeSelectedBytes()
    {
        SelectedBytes = Categories
            .Where(c => c.IsSelected && c.Scanned && !c.NotDetected && c.SizeBytes > 0)
            .Sum(c => c.SizeBytes);
    }

    private Task ScanAsync() => ScanAsync(clearResult: true);

    /// <param name="clearResult">False for the automatic rescan after a clean, so the
    /// "Freed …" result strip (and its audit-log button) stays visible.</param>
    private async Task ScanAsync(bool clearResult)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        if (clearResult)
        {
            ResultSummary = null;
        }

        _setStatus("Scanning…");

        try
        {
            foreach (CategoryItemViewModel item in Categories)
            {
                item.Scanned = false;
                item.SizeBytes = -1;
                item.ScanNote = null;
            }

            List<CleanCategory> toScan = [.. Categories.Select(c => c.Category)];
            var progress = new Progress<ScanProgress>(p =>
                ProgressText = $"Scanned {p.CategoriesDone} of {p.CategoriesTotal} — {ByteFormatter.Format(p.BytesFoundSoFar)} found");

            Dictionary<string, CategoryScanResult> scans = await _engine.ScanAsync(toScan, _settings, progress, cancellationToken: cts.Token);
            _lastScan = scans;

            foreach (CategoryItemViewModel item in Categories)
            {
                if (!scans.TryGetValue(item.Category.Id, out CategoryScanResult? scan))
                {
                    continue;
                }

                item.Scanned = true;
                item.SizeBytes = scan.TotalBytes;
                item.FileCount = scan.FileCount;
                item.NotDetected = scan.NotDetected;

                var notes = new List<string>();
                if (scan.SkippedTooNew > 0)
                {
                    notes.Add($"{scan.SkippedTooNew:N0} recent files kept");
                }

                if (scan.SkippedReparsePoints > 0)
                {
                    notes.Add($"{scan.SkippedReparsePoints} junctions skipped");
                }

                if (scan.Inaccessible > 0)
                {
                    notes.Add($"{scan.Inaccessible} inaccessible");
                }

                item.ScanNote = notes.Count > 0 ? string.Join(" · ", notes) : null;
            }

            HasScanned = true;
            RecomputeSelectedBytes();
            long total = scans.Values.Sum(s => s.TotalBytes);
            ProgressText = string.Empty;
            _setStatus($"Scan complete — {ByteFormatter.Format(total)} reclaimable found");
        }
        catch (OperationCanceledException)
        {
            // Items were reset at the start of the scan; keep the footer/Clean button honest.
            HasScanned = Categories.Any(c => c.Scanned && !c.NotDetected);
            RecomputeSelectedBytes();
            ProgressText = string.Empty;
            _setStatus("Scan cancelled");
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private Task RequestCleanAsync()
    {
        List<CategoryItemViewModel> selected = [.. Categories.Where(c => c.IsSelected && c.Scanned && !c.NotDetected && c.SizeBytes > 0)];
        if (selected.Count == 0)
        {
            return Task.CompletedTask;
        }

        var moderatePlus = selected.Where(c => c.Risk != RiskLevel.Safe).ToList();
        string warningBlock = moderatePlus.Count == 0
            ? string.Empty
            : "\n\nHeads-up for:\n" + string.Join("\n", moderatePlus.Select(c => $"  • {c.Name}: {c.Warning ?? "regenerable, but with a cost"}"));

        ConfirmText =
            $"Delete {ByteFormatter.Format(SelectedBytes)} across {selected.Count} categories?" +
            (_settings.SimulateOnly ? "\n\nSIMULATION MODE is on — BitBroom will only log what it would delete." : string.Empty) +
            warningBlock +
            "\n\nEvery deleted file is recorded in an audit log. Files in use and anything younger than " +
            $"{_settings.MinAgeHours}h (temp categories) are skipped automatically.";

        if (_settings.ConfirmBeforeClean)
        {
            ConfirmVisible = true;
            return Task.CompletedTask;
        }

        return RunCleanGuardedAsync();
    }

    /// <summary>Wraps the clean so exceptions on the confirm (fire-and-forget) path still surface to the user.</summary>
    private async Task RunCleanGuardedAsync()
    {
        try
        {
            await ExecuteCleanAsync();
        }
        catch (OperationCanceledException)
        {
            // Already handled inside ExecuteCleanAsync.
        }
        catch (Exception ex)
        {
            _setStatus($"Clean failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Clean failed: {ex.Message}", "BitBroom",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task ExecuteCleanAsync()
    {
        if (_lastScan is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        _setStatus(_settings.SimulateOnly ? "Simulating clean…" : "Cleaning…");

        try
        {
            List<CleanCategory> selected = [.. Categories
                .Where(c => c.IsSelected && c.Scanned && !c.NotDetected && c.SizeBytes > 0)
                .Select(c => c.Category)];

            if (_settings.CreateRestorePointBeforeClean && !_settings.SimulateOnly)
            {
                ProgressText = "Creating restore point…";
                (_, string message) = await SystemTools.TryCreateRestorePointAsync(cts.Token);
                _setStatus(message);
            }

            using var logger = new RunLogger(AppSettings.LogsDirectory, _settings.SimulateOnly ? "dryrun" : "clean");
            var progress = new Progress<CleanProgress>(p =>
                ProgressText = $"{p.CategoryName}: {p.ItemsDone:N0}/{p.ItemsTotal:N0} — {ByteFormatter.Format(p.BytesFreedSoFar)} freed");

            Dictionary<string, CategoryCleanResult> results = await _engine.CleanAsync(
                selected, _lastScan, _settings, logger, progress, cancellationToken: cts.Token);

            long freed = results.Values.Sum(r => r.BytesFreed);
            int deleted = results.Values.Sum(r => r.Deleted);
            int locked = results.Values.Sum(r => r.Locked);
            int denied = results.Values.Sum(r => r.AccessDenied);

            if (!_settings.SimulateOnly)
            {
                _settings.LifetimeBytesFreed += freed;
                _settings.LifetimeItemsDeleted += deleted;
                _settings.LastCleanUtc = DateTime.UtcNow;
                _settings.Save();
            }

            ResultSummary =
                $"{(_settings.SimulateOnly ? "Would free" : "Freed")} {ByteFormatter.Format(freed)} · {deleted:N0} items" +
                (locked > 0 ? $" · {locked:N0} in use (skipped)" : string.Empty) +
                (denied > 0 ? $" · {denied:N0} access denied" : string.Empty);
            ResultLogPath = logger.LogFilePath;
            ProgressText = string.Empty;
            _setStatus(ResultSummary);

            // Refresh sizes and drive stats after a real clean (keep the result strip visible).
            if (!_settings.SimulateOnly)
            {
                OnCleanCompleted?.Invoke();
                await ScanAsync(clearResult: false);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
            _setStatus("Clean cancelled");
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }
}
