using System.IO;
using System.Windows;
using System.Windows.Threading;
using BitBroom.Core.Settings;

namespace BitBroom.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "BitBroom.App.SingleInstance", out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("BitBroom is already running.", "BitBroom", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        int? initialTab = null;
        bool autoScan = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--tab", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int tab))
            {
                initialTab = tab;
            }
            else if (string.Equals(e.Args[i], "--autoscan", StringComparison.OrdinalIgnoreCase))
            {
                autoScan = true;
            }
        }

        // Brand accent for Fluent controls (Primary buttons, toggles, selection).
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);

        var window = new MainWindow(initialTab, autoScan);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"BitBroom hit an unexpected error and wrote a crash log to:\n{AppSettings.LogsDirectory}\n\n{e.Exception.Message}",
            "BitBroom",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            string path = Path.Combine(AppSettings.LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
        }
        catch (Exception)
        {
            // Crash logging is best-effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Releasing a mutex we never acquired (second instance) throws — only the owner releases.
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
