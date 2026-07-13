using System.Collections.ObjectModel;
using System.IO;
using BitBroom.App.Mvvm;
using BitBroom.Core.Analyzer;
using BitBroom.Core.Native;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

public sealed class AnalyzerNodeViewModel : ObservableObject
{
    private readonly AnalyzerNode _node;
    private readonly long _grandTotal;
    private ObservableCollection<AnalyzerNodeViewModel>? _children;

    public AnalyzerNodeViewModel(AnalyzerNode node, long grandTotal)
    {
        _node = node;
        _grandTotal = grandTotal;
    }

    public string Name => _node.Name;
    public string FullPath => _node.FullPath;
    public long SizeBytes => _node.SizeBytes;
    public string SizeText => ByteFormatter.Format(_node.SizeBytes);
    public string FileCountText => $"{_node.FileCount:N0} files";
    public double Share => _grandTotal > 0 ? (double)_node.SizeBytes / _grandTotal : 0;
    public string ShareText => $"{Share * 100:0.0}%";
    public double BarWidth => Math.Max(Share * 120, _node.SizeBytes > 0 ? 2 : 0);
    public bool WasInaccessible => _node.WasInaccessible;

    /// <summary>Children materialize lazily so huge trees stay cheap for the UI.</summary>
    public ObservableCollection<AnalyzerNodeViewModel> Children
        => _children ??= [.. _node.Children.Where(c => c.SizeBytes > 0 || c.Children.Count > 0)
            .Take(200)
            .Select(c => new AnalyzerNodeViewModel(c, _grandTotal))];
}

public sealed class LargeFileViewModel
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public string SizeText => ByteFormatter.Format(SizeBytes);
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;
}

public sealed class FileTypeViewModel
{
    public required FileTypeStat Stat { get; init; }
    public required long GrandTotal { get; init; }

    public string Extension => Stat.Extension;
    public string SizeText => ByteFormatter.Format(Stat.TotalBytes);
    public string CountText => $"{Stat.FileCount:N0} files";
    public double Share => GrandTotal > 0 ? (double)Stat.TotalBytes / GrandTotal : 0;
    public double BarWidth => Math.Max(Share * 90, Stat.TotalBytes > 0 ? 2 : 0);
}

public sealed class AnalyzerViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private CancellationTokenSource? _cts;

    private bool _isBusy;
    private string _targetPath;
    private string _progressText = string.Empty;
    private string? _summaryText;
    private AnalyzerNodeViewModel? _root;

    public ObservableCollection<string> DriveChoices { get; } = [];
    public ObservableCollection<AnalyzerNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<LargeFileViewModel> LargestFiles { get; } = [];
    public ObservableCollection<FileTypeViewModel> FileTypes { get; } = [];

    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand RecycleCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    public AnalyzerViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            DriveChoices.Add(drive.RootDirectory.FullName);
        }

        string? profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            DriveChoices.Add(profile);
        }

        _targetPath = DriveChoices.FirstOrDefault() ?? @"C:\";

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !_isBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _isBusy);
        OpenInExplorerCommand = new RelayCommand(parameter =>
        {
            string? path = PathOf(parameter);
            if (path is null)
            {
                return;
            }

            string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        });
        RecycleCommand = new RelayCommand(async parameter =>
        {
            string? path = PathOf(parameter);
            if (path is null)
            {
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"Send to Recycle Bin?\n\n{path}\n\nYou can restore it from the bin afterwards.",
                "BitBroom — Analyzer",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            // Run off the UI thread — recycling a large folder can take seconds.
            _setStatus($"Sending to Recycle Bin: {path}…");
            int hr = await Task.Run(() => NativeMethods.SendToRecycleBin(path));
            _setStatus(hr == 0 ? $"Sent to Recycle Bin: {path}" : $"Could not recycle (error {hr}): {path}");
        });
        ExportCsvCommand = new RelayCommand(ExportCsv, () => LargestFiles.Count > 0 || FileTypes.Count > 0);
    }

    /// <summary>Exports largest files + type breakdown to a CSV the user picks.</summary>
    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export analysis as CSV",
            FileName = $"bitbroom-analysis-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var largest = LargestFiles.Select(f => new LargeFile(f.Path, f.SizeBytes, DateTime.MinValue));
            var types = FileTypes.Select(t => t.Stat);
            string csv = AnalyzerCsv.Build(largest, types);

            File.WriteAllText(dialog.FileName, csv, System.Text.Encoding.UTF8);
            _setStatus($"Exported analysis to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _setStatus($"Export failed: {ex.Message}");
        }
    }

    private static string? PathOf(object? parameter) => parameter switch
    {
        AnalyzerNodeViewModel node => node.FullPath,
        LargeFileViewModel file => file.Path,
        string s => s,
        _ => null,
    };

    public string TargetPath
    {
        get => _targetPath;
        set => SetProperty(ref _targetPath, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    private async Task AnalyzeAsync()
    {
        string target = TargetPath.Trim();
        if (!Directory.Exists(target))
        {
            _setStatus($"Folder not found: {target}");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        RootNodes.Clear();
        LargestFiles.Clear();
        FileTypes.Clear();
        SummaryText = null;
        _setStatus($"Analyzing {target}…");

        try
        {
            var analyzer = new DiskAnalyzer();
            var progress = new Progress<AnalyzerProgress>(p =>
                ProgressText = $"{p.FilesScanned:N0} files · {ByteFormatter.Format(p.BytesSoFar)} · {Shorten(p.CurrentPath)}");

            AnalyzerResult result = await Task.Run(() => analyzer.AnalyzeAsync(target, progress, _cts.Token));

            _root = new AnalyzerNodeViewModel(result.Root, result.TotalBytes);
            foreach (AnalyzerNodeViewModel child in _root.Children)
            {
                RootNodes.Add(child);
            }

            foreach (LargeFile file in result.LargestFiles.Take(100))
            {
                LargestFiles.Add(new LargeFileViewModel { Path = file.Path, SizeBytes = file.SizeBytes });
            }

            foreach (FileTypeStat stat in result.FileTypes.Take(15))
            {
                FileTypes.Add(new FileTypeViewModel { Stat = stat, GrandTotal = result.TotalBytes });
            }

            ExportCsvCommand.RaiseCanExecuteChanged();

            SummaryText = $"{ByteFormatter.Format(result.TotalBytes)} in {result.TotalFiles:N0} files · " +
                          $"{result.Duration.TotalSeconds:0.0}s · {result.SkippedReparsePoints} junctions skipped" +
                          (result.InaccessibleDirectories > 0 ? $" · {result.InaccessibleDirectories} folders inaccessible" : string.Empty);
            ProgressText = string.Empty;
            _setStatus("Analysis complete");
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
            _setStatus("Analysis cancelled");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string Shorten(string path)
        => path.Length <= 60 ? path : path[..28] + "…" + path[^30..];
}
