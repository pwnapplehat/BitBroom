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
        typeof(DupesView),
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

        FitToWorkArea();

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
                        viewModel.Dupes.ScanCommand.Execute(null);
                        break;
                    case 3:
                        viewModel.Analyzer.TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        viewModel.Analyzer.AnalyzeCommand.Execute(null);
                        break;
                    case 4:
                        viewModel.Hogs.InspectCommand.Execute(null);
                        break;
                }
            }
        };
    }

    /// <summary>
    /// Keeps the generous default size on big monitors but shrinks it to fit smaller
    /// screens (e.g. a 1366×768 laptop can't show a 910px-tall window). Work area is in
    /// device-independent units and already excludes the taskbar, so this is DPI-correct.
    /// </summary>
    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return;
        }

        // Leave a small margin so the window never touches the screen edges.
        double maxWidth = work.Width * 0.96;
        double maxHeight = work.Height * 0.96;

        Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The DWM backdrop only shows through transparent pixels: apply dark theme +
        // acrylic, then clear the window background so the taskbar-style wallpaper
        // blur comes through (with the smoke tint for contrast). When acrylic can't
        // apply — Windows 10, or transparency effects disabled in Settings — keep the
        // stock solid dark background instead. The backdrop is deliberately driven
        // from here (XAML says None) so DWM never paints its own washed-out acrylic
        // fallback over our solid background.
        bool wantAcrylic = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantAcrylic ? WindowBackdropType.Acrylic : WindowBackdropType.None,
            updateAccent: false);

        if (wantAcrylic && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            WindowBackdropType = WindowBackdropType.Acrylic;
            Background = Brushes.Transparent;
            SmokeTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            // Stock WPF UI dark background (#FF202020) — the native Windows grey.
            Background = solid;
        }
    }

    /// <summary>Settings → Personalization → Colors → "Transparency effects".</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
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

            if (DataContext is MainViewModel { PlayStartupSound: true })
            {
                PlayStartupSound();
            }
        }
        catch (Exception)
        {
            // The intro is decorative — never let it block the app.
            Shell.Opacity = 1;
            Splash.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Plays the broom-sweep intro sound, timed to the splash animation.</summary>
    private static void PlayStartupSound()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/sounds/startup.wav"));
            if (resource is null)
            {
                return;
            }

            var player = new System.Media.SoundPlayer(resource.Stream);
            // Async play; keep the player (and its stream) alive until it finishes.
            player.Play();
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
            {
                player.Dispose();
                resource.Stream.Dispose();
            });
        }
        catch (Exception)
        {
            // Sound is a garnish — any audio failure must never affect startup.
        }
    }
}
