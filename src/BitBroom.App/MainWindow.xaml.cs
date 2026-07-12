using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BitBroom.App.Services;
using BitBroom.App.ViewModels;
using BitBroom.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Interop;

namespace BitBroom.App;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] TabPages =
    [
        typeof(DashboardView),
        typeof(CleanView),
        typeof(AnalyzerView),
        typeof(HogsView),
        typeof(ToolsView),
        typeof(SettingsView),
        typeof(AboutView),
    ];

    private bool _splashPlayed;

    public MainWindow(int? initialTab = null, bool autoScan = false)
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;

        RootNavigation.SetPageProviderService(new PageService(viewModel));
        viewModel.NavigateRequested += tab => NavigateToTab(tab);

        Loaded += (_, _) =>
        {
            int tab = initialTab is int t && t >= 0 && t < TabPages.Length ? t : 0;
            NavigateToTab(tab);
            PlaySplash();

            if (autoScan)
            {
                // Read-only scan on launch (used for docs screenshots and smoke testing).
                switch (tab)
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

        // The DWM backdrop only shows through transparent pixels: apply dark theme +
        // acrylic, then clear the window background so the taskbar-style wallpaper
        // blur comes through. On Windows 10 (no acrylic backdrop) the solid dark
        // ApplicationBackgroundBrush stays in place.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Acrylic, updateAccent: false);
        if (WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            Background = Brushes.Transparent;
        }
    }

    private void NavigateToTab(int tab)
    {
        if (tab >= 0 && tab < TabPages.Length)
        {
            RootNavigation.Navigate(TabPages[tab]);
        }
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
}
