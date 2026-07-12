using System.Collections.ObjectModel;
using BitBroom.App.Mvvm;
using BitBroom.Core.Hogs;
using BitBroom.Core.Util;

namespace BitBroom.App.ViewModels;

public sealed class HogItemViewModel
{
    public required HogItem Item { get; init; }

    public string Title => Item.Title;
    public string SizeText => Item.SizeBytes.HasValue ? ByteFormatter.Format(Item.SizeBytes.Value) : "";
    public string Detail => Item.Detail;
    public string Guidance => Item.Guidance;
    public HogSeverity Severity => Item.Severity;
    public string SeverityLabel => Item.Severity switch
    {
        HogSeverity.Critical => "CRITICAL",
        HogSeverity.Notable => "NOTABLE",
        _ => "INFO",
    };

    public bool HasPath => !string.IsNullOrEmpty(Item.Path);
    public string? Path => Item.Path;
}

public sealed class HogsViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private bool _isBusy;
    private bool _hasRun;

    public ObservableCollection<HogItemViewModel> Hogs { get; } = [];
    public AsyncRelayCommand InspectCommand { get; }
    public RelayCommand OpenPathCommand { get; }

    public HogsViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;
        InspectCommand = new AsyncRelayCommand(InspectAsync, () => !_isBusy);
        OpenPathCommand = new RelayCommand(parameter =>
        {
            if (parameter is HogItemViewModel { Path: { } path })
            {
                string arguments = System.IO.File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
        });
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                InspectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasRun
    {
        get => _hasRun;
        private set => SetProperty(ref _hasRun, value);
    }

    public bool NothingFound => HasRun && Hogs.Count == 0;

    private async Task InspectAsync()
    {
        IsBusy = true;
        _setStatus("Inspecting hidden space hogs…");
        Hogs.Clear();

        try
        {
            var inspector = new SpaceHogInspector();
            List<HogItem> items = await inspector.InspectAsync();
            foreach (HogItem item in items)
            {
                Hogs.Add(new HogItemViewModel { Item = item });
            }

            HasRun = true;
            OnPropertyChanged(nameof(NothingFound));
            _setStatus($"Space hog inspection complete — {items.Count} findings");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
