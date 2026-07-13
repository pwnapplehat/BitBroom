using System.Collections.ObjectModel;
using System.IO;
using BitBroom.App.Mvvm;
using BitBroom.Core.Dupes;
using BitBroom.Core.Engine;
using BitBroom.Core.Logging;
using BitBroom.Core.Settings;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

/// <summary>One file row inside a duplicate group.</summary>
public sealed class DupeFileViewModel : ObservableObject
{
    private bool _isSelected;

    public required DuplicateFile File { get; init; }
    public required DupeGroupViewModel Group { get; init; }

    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Directory => System.IO.Path.GetDirectoryName(File.Path) ?? File.Path;
    public string Path => File.Path;
    public string ModifiedText => File.LastWriteUtc == DateTime.MinValue ? "" : File.LastWriteUtc.ToLocalTime().ToString("d MMM yyyy HH:mm");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // Keep-one enforcement at the source: the last unselected copy cannot be selected.
            if (value && Group.SelectedCount >= Group.Files.Count - 1)
            {
                Group.Owner.NotifyKeepOneBlocked();
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                Group.Owner.OnSelectionChanged();
            }
        }
    }

    /// <summary>Programmatic set that bypasses the guard (used by auto-select which respects it globally).</summary>
    internal void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

public sealed class DupeGroupViewModel
{
    public required DuplicateGroup Group { get; init; }
    public required DupesViewModel Owner { get; init; }
    public List<DupeFileViewModel> Files { get; } = [];

    public string HeaderText => $"{Files.Count} copies · {ByteFormatter.Format(Group.FileSizeBytes)} each · {ByteFormatter.Format(Group.WastedBytes)} reclaimable";
    public int SelectedCount => Files.Count(f => f.IsSelected);
}

public sealed class EmptyFolderViewModel : ObservableObject
{
    private bool _isSelected;

    public required string Path { get; init; }
    public required DupesViewModel Owner { get; init; }

    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } leaf ? leaf : Path;
    public string Parent => System.IO.Path.GetDirectoryName(Path) ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Owner.OnSelectionChanged();
            }
        }
    }
}

/// <summary>One regenerable developer folder (node_modules, target, .venv, …).</summary>
public sealed class DevArtifactViewModel : ObservableObject
{
    private bool _isSelected;

    public required DevArtifact Artifact { get; init; }
    public required DupesViewModel Owner { get; init; }

    public string Path => Artifact.Path;
    public string Name => System.IO.Path.GetFileName(Artifact.Path) is { Length: > 0 } leaf ? leaf : Artifact.Path;
    public string Parent => System.IO.Path.GetDirectoryName(Artifact.Path) ?? "";
    public string Kind => Artifact.Kind;
    public string SizeText => ByteFormatter.Format(Artifact.SizeBytes);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Owner.OnSelectionChanged();
            }
        }
    }
}

/// <summary>The three things the tab can hunt for.</summary>
public enum DupesScanMode
{
    DuplicateFiles = 0,
    EmptyFolders = 1,
    DevJunk = 2,
}

/// <summary>
/// The Duplicates tab: content-verified duplicate files, empty folders, and regenerable
/// developer build folders (node_modules, target, .venv, …) under a user-chosen root.
/// Deletion is Recycle Bin-only and one copy of every duplicate group always survives
/// (enforced in the Core deleter, mirrored in the UI).
/// </summary>
public sealed class DupesViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _hasRun;
    private DupesScanMode _mode;
    private string _targetPath;
    private string _progressText = string.Empty;
    private string? _summaryText;
    private long _selectedBytes;
    private int _selectedCount;
    private bool _confirmVisible;
    private string _confirmText = string.Empty;
    private string? _resultLogPath;
    private int _minSizeIndex = 1;

    public ObservableCollection<DupeGroupViewModel> Groups { get; } = [];
    public ObservableCollection<EmptyFolderViewModel> EmptyFolders { get; } = [];
    public ObservableCollection<DevArtifactViewModel> DevArtifacts { get; } = [];
    public ObservableCollection<string> ScanRootChoices { get; } = [];

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand KeepNewestCommand { get; }
    public RelayCommand KeepOldestCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectAllEmptyCommand { get; }
    public RelayCommand SelectAllDevCommand { get; }
    public RelayCommand RecycleCommand { get; }
    public RelayCommand ConfirmRecycleCommand { get; }
    public RelayCommand CancelConfirmCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public DupesViewModel(AppSettings settings, Action<string> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            ScanRootChoices.Add(drive.Name);
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            ScanRootChoices.Insert(0, profile);
        }

        _targetPath = ScanRootChoices.FirstOrDefault() ?? @"C:\";

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_isBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _isBusy);
        BrowseCommand = new RelayCommand(Browse);
        KeepNewestCommand = new RelayCommand(() => AutoSelect(keepNewest: true), () => Groups.Count > 0 && !_isBusy);
        KeepOldestCommand = new RelayCommand(() => AutoSelect(keepNewest: false), () => Groups.Count > 0 && !_isBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !_isBusy);
        SelectAllEmptyCommand = new RelayCommand(() =>
        {
            foreach (EmptyFolderViewModel folder in EmptyFolders)
            {
                folder.IsSelected = true;
            }
        }, () => EmptyFolders.Count > 0 && !_isBusy);
        SelectAllDevCommand = new RelayCommand(() =>
        {
            foreach (DevArtifactViewModel artifact in DevArtifacts)
            {
                artifact.IsSelected = true;
            }
        }, () => DevArtifacts.Count > 0 && !_isBusy);
        RecycleCommand = new RelayCommand(RequestRecycle, () => !_isBusy && _selectedCount > 0);
        ConfirmRecycleCommand = new RelayCommand(() => { ConfirmVisible = false; _ = RunRecycleGuardedAsync(); });
        CancelConfirmCommand = new RelayCommand(() => ConfirmVisible = false);
        OpenInExplorerCommand = new RelayCommand(parameter =>
        {
            string? path = parameter switch
            {
                DupeFileViewModel file => file.Path,
                EmptyFolderViewModel folder => folder.Path,
                DevArtifactViewModel artifact => artifact.Path,
                _ => null,
            };
            if (path is not null)
            {
                string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
        });
        OpenLogCommand = new RelayCommand(() =>
        {
            if (_resultLogPath is not null && File.Exists(_resultLogPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_resultLogPath) { UseShellExecute = true });
            }
        });
    }

    // -------------------------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                RecycleCommand.RaiseCanExecuteChanged();
                KeepNewestCommand.RaiseCanExecuteChanged();
                KeepOldestCommand.RaiseCanExecuteChanged();
                ClearSelectionCommand.RaiseCanExecuteChanged();
                SelectAllEmptyCommand.RaiseCanExecuteChanged();
                SelectAllDevCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DupesScanMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(DupesMode));
                OnPropertyChanged(nameof(EmptyFoldersMode));
                OnPropertyChanged(nameof(DevJunkMode));
                OnPropertyChanged(nameof(ModeHint));
                SummaryText = null;
                OnSelectionChanged();
            }
        }
    }

    public bool DupesMode
    {
        get => _mode == DupesScanMode.DuplicateFiles;
        set
        {
            if (value)
            {
                Mode = DupesScanMode.DuplicateFiles;
            }
        }
    }

    public bool EmptyFoldersMode
    {
        get => _mode == DupesScanMode.EmptyFolders;
        set
        {
            if (value)
            {
                Mode = DupesScanMode.EmptyFolders;
            }
        }
    }

    public bool DevJunkMode
    {
        get => _mode == DupesScanMode.DevJunk;
        set
        {
            if (value)
            {
                Mode = DupesScanMode.DevJunk;
            }
        }
    }

    public string ModeHint => _mode switch
    {
        DupesScanMode.EmptyFolders =>
            "Folders with no files anywhere inside (nested empty folders count). Junction holders are never listed.",
        DupesScanMode.DevJunk =>
            "Regenerable developer folders — node_modules, target, .venv, dist, .next and friends. Only flagged next to a real project manifest; the next build/install recreates them.",
        _ => "Files with byte-identical content (verified by full hash). One copy of every group always survives.",
    };

    public string TargetPath
    {
        get => _targetPath;
        set => SetProperty(ref _targetPath, value);
    }

    /// <summary>0 = 0 (all sizes), 1 = 1 MB, 2 = 10 MB, 3 = 100 MB.</summary>
    public int MinSizeIndex
    {
        get => _minSizeIndex;
        set => SetProperty(ref _minSizeIndex, value);
    }

    private long MinSizeBytes => _minSizeIndex switch
    {
        0 => 1,
        2 => 10L * 1024 * 1024,
        3 => 100L * 1024 * 1024,
        _ => 1024L * 1024,
    };

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string? SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string SelectedText => _mode switch
    {
        DupesScanMode.EmptyFolders => $"{_selectedCount:N0} folders selected",
        DupesScanMode.DevJunk => $"{_selectedCount:N0} folders · {ByteFormatter.Format(_selectedBytes)} selected",
        _ => $"{_selectedCount:N0} files · {ByteFormatter.Format(_selectedBytes)} selected",
    };

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

    public string? ResultLogPath
    {
        get => _resultLogPath;
        private set => SetProperty(ref _resultLogPath, value);
    }

    public bool NothingFound => _hasRun && !_isBusy && _mode switch
    {
        DupesScanMode.EmptyFolders => EmptyFolders.Count == 0,
        DupesScanMode.DevJunk => DevArtifacts.Count == 0,
        _ => Groups.Count == 0,
    };

    // -------------------------------------------------------------------------

    internal void OnSelectionChanged()
    {
        switch (_mode)
        {
            case DupesScanMode.EmptyFolders:
                _selectedCount = EmptyFolders.Count(f => f.IsSelected);
                _selectedBytes = 0;
                break;
            case DupesScanMode.DevJunk:
                _selectedCount = DevArtifacts.Count(a => a.IsSelected);
                _selectedBytes = DevArtifacts.Where(a => a.IsSelected).Sum(a => a.Artifact.SizeBytes);
                break;
            default:
                _selectedCount = Groups.Sum(g => g.SelectedCount);
                _selectedBytes = Groups.Sum(g => g.Files.Where(f => f.IsSelected).Sum(_ => g.Group.FileSizeBytes));
                break;
        }

        OnPropertyChanged(nameof(SelectedText));
        RecycleCommand.RaiseCanExecuteChanged();
    }

    internal void NotifyKeepOneBlocked()
        => _setStatus("At least one copy of every duplicate group must be kept.");

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder to scan",
            InitialDirectory = Directory.Exists(TargetPath) ? TargetPath : null,
        };

        if (dialog.ShowDialog() == true)
        {
            TargetPath = dialog.FolderName;
        }
    }

    private async Task ScanAsync()
    {
        if (!Directory.Exists(TargetPath))
        {
            _setStatus($"Folder not found: {TargetPath}");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        SummaryText = null;
        Groups.Clear();
        EmptyFolders.Clear();
        DevArtifacts.Clear();
        OnSelectionChanged();

        try
        {
            var exclusions = new ExclusionSet(_settings.ExcludedPaths);

            if (DevJunkMode)
            {
                _setStatus("Scanning for developer build folders…");
                ProgressText = "Scanning…";
                var finder = new DevArtifactFinder(exclusions);
                var progress = new Progress<DevArtifactProgress>(p =>
                    ProgressText = $"{p.DirectoriesScanned:N0} folders scanned — {p.Found:N0} found");

                DevArtifactScanResult result = await finder.ScanAsync(TargetPath, progress, cts.Token);

                foreach (DevArtifact artifact in result.Artifacts)
                {
                    if (DuplicateDeleter.ValidatePath(artifact.Path) is null)
                    {
                        DevArtifacts.Add(new DevArtifactViewModel { Artifact = artifact, Owner = this });
                    }
                }

                SummaryText = $"{DevArtifacts.Count:N0} regenerable dev folders · {ByteFormatter.Format(result.TotalBytes)} reclaimable " +
                              $"({result.DirectoriesScanned:N0} folders scanned · {result.Duration.TotalSeconds:0.0}s)";
                _setStatus($"Dev junk scan complete — {ByteFormatter.Format(result.TotalBytes)} reclaimable");
            }
            else if (EmptyFoldersMode)
            {
                _setStatus("Scanning for empty folders…");
                ProgressText = "Scanning…";
                var finder = new EmptyFolderFinder(exclusions);
                EmptyFolderScanResult result = await finder.ScanAsync(TargetPath, cts.Token);

                foreach (string folder in result.EmptyFolders)
                {
                    if (DuplicateDeleter.ValidatePath(folder) is null)
                    {
                        EmptyFolders.Add(new EmptyFolderViewModel { Path = folder, Owner = this });
                    }
                }

                SummaryText = $"{EmptyFolders.Count:N0} empty folders in {result.FoldersScanned:N0} scanned " +
                              $"({result.Duration.TotalSeconds:0.0}s · {result.SkippedReparsePoints} junctions treated as content)";
                _setStatus($"Empty folder scan complete — {EmptyFolders.Count:N0} found");
            }
            else
            {
                _setStatus("Scanning for duplicate files…");
                var finder = new DuplicateFinder(exclusions);
                var progress = new Progress<DuplicateScanProgress>(p =>
                    ProgressText = $"{p.Phase} — {p.FilesSeen:N0} files, {ByteFormatter.Format(p.BytesHashed)} hashed");

                DuplicateScanResult result = await finder.ScanAsync(TargetPath, MinSizeBytes, progress, cts.Token);

                foreach (DuplicateGroup group in result.Groups)
                {
                    var groupVm = new DupeGroupViewModel { Group = group, Owner = this };
                    foreach (DuplicateFile file in group.Files)
                    {
                        groupVm.Files.Add(new DupeFileViewModel { File = file, Group = groupVm });
                    }

                    Groups.Add(groupVm);
                }

                SummaryText = $"{Groups.Count:N0} duplicate groups · {ByteFormatter.Format(result.TotalWastedBytes)} reclaimable " +
                              $"({result.FilesConsidered:N0} files considered · {ByteFormatter.Format(result.BytesHashed)} hashed · {result.Duration.TotalSeconds:0.0}s)";
                _setStatus($"Duplicate scan complete — {ByteFormatter.Format(result.TotalWastedBytes)} reclaimable");
            }

            _hasRun = true;
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Scan cancelled.";
            _setStatus("Scan cancelled");
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
            _setStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            OnPropertyChanged(nameof(NothingFound));
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private void AutoSelect(bool keepNewest)
    {
        foreach (DupeGroupViewModel group in Groups)
        {
            // Keep exactly one: the newest (or oldest) by last-write; files arrive newest-first.
            DupeFileViewModel keeper = keepNewest
                ? group.Files.OrderByDescending(f => f.File.LastWriteUtc).First()
                : group.Files.OrderBy(f => f.File.LastWriteUtc).First();

            foreach (DupeFileViewModel file in group.Files)
            {
                file.SetSelectedSilently(!ReferenceEquals(file, keeper));
            }
        }

        OnSelectionChanged();
    }

    private void ClearSelection()
    {
        foreach (DupeGroupViewModel group in Groups)
        {
            foreach (DupeFileViewModel file in group.Files)
            {
                file.SetSelectedSilently(false);
            }
        }

        foreach (EmptyFolderViewModel folder in EmptyFolders)
        {
            folder.IsSelected = false;
        }

        foreach (DevArtifactViewModel artifact in DevArtifacts)
        {
            artifact.IsSelected = false;
        }

        OnSelectionChanged();
    }

    private void RequestRecycle()
    {
        ConfirmText = _mode switch
        {
            DupesScanMode.EmptyFolders =>
                $"Send {_selectedCount:N0} empty folders to the Recycle Bin?\n\nEach folder is re-verified as file-free immediately before it is recycled. You can restore everything from the bin.",
            DupesScanMode.DevJunk =>
                $"Send {_selectedCount:N0} dev folders ({ByteFormatter.Format(_selectedBytes)}) to the Recycle Bin?\n\nThese are regenerable build/dependency folders — the next 'npm install' / 'cargo build' recreates them. Each is re-verified as a dev artifact immediately before recycling, everything is restorable from the bin, and it all lands in the audit log.",
            _ =>
                $"Send {_selectedCount:N0} duplicate files ({ByteFormatter.Format(_selectedBytes)}) to the Recycle Bin?\n\nOne copy of every group is kept — that's enforced by the engine, not just this dialog. Everything is restorable from the bin and recorded in the audit log.",
        };
        ConfirmVisible = true;
    }

    private async Task RunRecycleGuardedAsync()
    {
        try
        {
            await RecycleSelectedAsync();
        }
        catch (Exception ex)
        {
            _setStatus($"Recycle failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Recycle failed: {ex.Message}", "BitBroom",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task RecycleSelectedAsync()
    {
        IsBusy = true;
        _setStatus("Sending to Recycle Bin…");

        try
        {
            using var logger = new RunLogger(AppSettings.LogsDirectory, "dupes");
            var deleter = new DuplicateDeleter(logger);
            DuplicateDeleteResult result;

            if (DevJunkMode)
            {
                List<DevArtifact> selected = [.. DevArtifacts.Where(a => a.IsSelected).Select(a => a.Artifact)];
                var progress = new Progress<int>(done => ProgressText = $"Recycled {done:N0} of {selected.Count:N0}…");
                result = await Task.Run(() => deleter.RecycleDevArtifacts(selected, progress));

                for (int i = DevArtifacts.Count - 1; i >= 0; i--)
                {
                    if (!Directory.Exists(DevArtifacts[i].Path))
                    {
                        DevArtifacts.RemoveAt(i);
                    }
                }

                SummaryText = $"Recycled {result.Recycled:N0} dev folders · {ByteFormatter.Format(result.BytesRecycled)} reclaimable once the bin is emptied" +
                              (result.Failed > 0 ? $" · {result.Failed} failed" : string.Empty) +
                              (result.RefusedByGuard > 0 ? $" · {result.RefusedByGuard} refused" : string.Empty);
            }
            else if (EmptyFoldersMode)
            {
                List<string> selected = [.. EmptyFolders.Where(f => f.IsSelected).Select(f => f.Path)];
                var progress = new Progress<int>(done => ProgressText = $"Recycled {done:N0} of {selected.Count:N0}…");
                result = await Task.Run(() => deleter.RecycleEmptyFolders(selected, progress));

                // Remove recycled folders (and any descendants that vanished with them).
                for (int i = EmptyFolders.Count - 1; i >= 0; i--)
                {
                    if (!Directory.Exists(EmptyFolders[i].Path))
                    {
                        EmptyFolders.RemoveAt(i);
                    }
                }

                SummaryText = $"Recycled {result.Recycled:N0} folders" +
                              (result.Failed > 0 ? $" · {result.Failed} failed" : string.Empty) +
                              (result.RefusedByGuard > 0 ? $" · {result.RefusedByGuard} refused" : string.Empty);
            }
            else
            {
                var selections = new List<(DuplicateGroup, IReadOnlyList<DuplicateFile>)>();
                foreach (DupeGroupViewModel group in Groups)
                {
                    List<DuplicateFile> selected = [.. group.Files.Where(f => f.IsSelected).Select(f => f.File)];
                    if (selected.Count > 0)
                    {
                        selections.Add((group.Group, selected));
                    }
                }

                int total = selections.Sum(s => s.Item2.Count);
                var progress = new Progress<int>(done => ProgressText = $"Recycled {done:N0} of {total:N0}…");
                result = await Task.Run(() => deleter.RecycleSelected(selections, progress));

                // Rebuild group list: drop recycled files, then drop groups that no longer have 2+ copies.
                for (int i = Groups.Count - 1; i >= 0; i--)
                {
                    DupeGroupViewModel group = Groups[i];
                    for (int j = group.Files.Count - 1; j >= 0; j--)
                    {
                        if (!File.Exists(group.Files[j].Path))
                        {
                            group.Files.RemoveAt(j);
                        }
                    }

                    if (group.Files.Count < 2)
                    {
                        Groups.RemoveAt(i);
                    }
                }

                // Force the ItemsControl to re-read group contents.
                List<DupeGroupViewModel> snapshot = [.. Groups];
                Groups.Clear();
                foreach (DupeGroupViewModel group in snapshot)
                {
                    Groups.Add(group);
                }

                SummaryText = $"Recycled {result.Recycled:N0} files · {ByteFormatter.Format(result.BytesRecycled)} reclaimable once the bin is emptied" +
                              (result.Failed > 0 ? $" · {result.Failed} failed" : string.Empty) +
                              (result.GroupsRefusedKeepOne > 0 ? $" · {result.GroupsRefusedKeepOne} groups refused (keep-one)" : string.Empty);
            }

            ResultLogPath = logger.LogFilePath;
            ClearSelection();
            _setStatus(SummaryText ?? "Done");
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            OnPropertyChanged(nameof(NothingFound));
        }
    }
}
