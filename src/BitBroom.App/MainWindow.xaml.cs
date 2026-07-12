using System.Windows;
using System.Windows.Media.Animation;
using BitBroom.App.Interop;
using BitBroom.App.ViewModels;

namespace BitBroom.App;

public partial class MainWindow : Window
{
    private bool _splashPlayed;

    public MainWindow(int? initialTab = null, bool autoScan = false)
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        if (initialTab is int tab && tab is >= 0 and <= 6)
        {
            viewModel.SelectedTab = tab;
        }

        Loaded += (_, _) =>
        {
            PlaySplash();

            if (autoScan)
            {
                // Read-only scan on launch (used for docs screenshots and smoke testing).
                switch (viewModel.SelectedTab)
                {
                    case 1:
                        viewModel.Clean.ScanCommand.Execute(null);
                        break;
                    case 2:
                        viewModel.Analyzer.TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        viewModel.Analyzer.AnalyzeCommand.Execute(null);
                        break;
                    case 3:
                        viewModel.Hogs.InspectCommand.Execute(null);
                        break;
                }
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Dwm.ApplyWindowStyling(this);
    }

    private void PlaySplash()
    {
        if (_splashPlayed)
        {
            return;
        }

        _splashPlayed = true;

        try
        {
            Shell.Opacity = 0;
            var intro = (Storyboard)Resources["SplashIntro"];
            intro.Completed += (_, _) =>
            {
                Splash.Visibility = Visibility.Collapsed;
            };
            intro.Begin(this);
        }
        catch (Exception)
        {
            // The intro is decorative — never let it block the app.
            Shell.Opacity = 1;
            Splash.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
