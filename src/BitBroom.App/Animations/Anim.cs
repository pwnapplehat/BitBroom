using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BitBroom.App.Animations;

public enum TransitionMode
{
    None,
    Fade,
    SlideFade,
    ZoomFade,
}

/// <summary>
/// Attached-property animation toolkit: page transitions, staggered list reveals,
/// value ramp-ups and smooth (animated) mouse-wheel scrolling. All animations run on
/// opacity/transform, which WPF composites on the render thread — no per-frame layout.
/// </summary>
public static class Anim
{
    // =========================================================================
    // Display refresh rate — WPF ticks animations at 60fps by default, which looks
    // subtly choppy on high-refresh monitors (120/144/180Hz). Every animation this
    // toolkit creates asks for the real refresh rate of the monitor the app window
    // is currently on. Cached briefly so wheel-rate queries stay free, but still
    // tracking monitor moves and display-settings changes.
    // =========================================================================

    private static readonly TimeSpan RefreshRateCacheTtl = TimeSpan.FromSeconds(5);
    private static int _displayRefreshRate;
    private static long _refreshRateExpiryTicks;

    /// <summary>
    /// Refresh rate (Hz) of the monitor hosting the main window, clamped 60–240.
    /// Falls back to the primary display, then to WPF's default 60.
    /// </summary>
    public static int DisplayRefreshRate
    {
        get
        {
            long now = Environment.TickCount64;
            if (_displayRefreshRate == 0 || now >= _refreshRateExpiryTicks)
            {
                _displayRefreshRate = QueryDisplayRefreshRate();
                _refreshRateExpiryTicks = now + (long)RefreshRateCacheTtl.TotalMilliseconds;
            }

            return _displayRefreshRate;
        }
    }

    private static int QueryDisplayRefreshRate()
    {
        try
        {
            var mode = default(DEVMODE);
            mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            const int ENUM_CURRENT_SETTINGS = -1;

            // Prefer the monitor the app window actually lives on (multi-monitor
            // setups routinely mix 60/144/180Hz panels).
            string? device = GetCurrentWindowMonitorDevice();
            if (device is not null
                && EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode)
                && mode.dmDisplayFrequency >= 60)
            {
                return Math.Min((int)mode.dmDisplayFrequency, 240);
            }

            // Fall back to the primary display.
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency >= 60)
            {
                return Math.Min((int)mode.dmDisplayFrequency, 240);
            }
        }
        catch (Exception)
        {
            // Fall through to the WPF default.
        }

        return 60;
    }

    /// <summary>Device name (e.g. \\.\DISPLAY2) of the monitor hosting the main window.</summary>
    private static string? GetCurrentWindowMonitorDevice()
    {
        try
        {
            Window? window = Application.Current?.MainWindow;
            if (window is null)
            {
                return null;
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            const uint MONITOR_DEFAULTTONEAREST = 2;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = default(MONITORINFOEX);
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            return GetMonitorInfo(monitor, ref info) ? info.szDevice : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Applies the display-matched frame rate to an animation/storyboard.</summary>
    public static T AtDisplayRate<T>(T timeline)
        where T : Timeline
    {
        Timeline.SetDesiredFrameRate(timeline, DisplayRefreshRate);
        return timeline;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    // =========================================================================
    // Transition — plays every time the element becomes visible.
    // =========================================================================

    public static readonly DependencyProperty TransitionProperty = DependencyProperty.RegisterAttached(
        "Transition", typeof(TransitionMode), typeof(Anim), new PropertyMetadata(TransitionMode.None, OnTransitionChanged));

    public static void SetTransition(DependencyObject element, TransitionMode value) => element.SetValue(TransitionProperty, value);

    public static TransitionMode GetTransition(DependencyObject element) => (TransitionMode)element.GetValue(TransitionProperty);

    private static void OnTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && (TransitionMode)e.NewValue != TransitionMode.None)
        {
            element.IsVisibleChanged += OnTransitionElementVisibleChanged;
        }
    }

    private static void OnTransitionElementVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && e.NewValue is true)
        {
            Play(element, GetTransition(element));
        }
    }

    public static void Play(FrameworkElement element, TransitionMode mode)
    {
        var fade = AtDisplayRate(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        switch (mode)
        {
            case TransitionMode.SlideFade:
            {
                TranslateTransform translate = EnsureTranslate(element);
                var slide = AtDisplayRate(new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(340))
                {
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
                });
                translate.BeginAnimation(TranslateTransform.YProperty, slide);
                break;
            }

            case TransitionMode.ZoomFade:
            {
                ScaleTransform scale = EnsureScale(element);
                var zoom = AtDisplayRate(new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
                });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
                break;
            }

            case TransitionMode.Fade:
            default:
                break;
        }
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        return translate;
    }

    private static ScaleTransform EnsureScale(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
        }

        return scale;
    }

    // =========================================================================
    // StaggerChildren — items of an ItemsControl cascade in as they are generated.
    // =========================================================================

    public static readonly DependencyProperty StaggerChildrenProperty = DependencyProperty.RegisterAttached(
        "StaggerChildren", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnStaggerChildrenChanged));

    public static void SetStaggerChildren(DependencyObject element, bool value) => element.SetValue(StaggerChildrenProperty, value);

    public static bool GetStaggerChildren(DependencyObject element) => (bool)element.GetValue(StaggerChildrenProperty);

    /// <summary>Per-container marker so recycled/re-generated containers animate only once.</summary>
    private static readonly DependencyProperty StaggeredProperty = DependencyProperty.RegisterAttached(
        "Staggered", typeof(bool), typeof(Anim), new PropertyMetadata(false));

    /// <summary>
    /// End of the reveal window (UTC ticks). Containers realized after it — i.e. rows a
    /// virtualizing panel creates while the user scrolls — appear instantly instead of
    /// fading in mid-scroll.
    /// </summary>
    private static readonly DependencyProperty StaggerDeadlineProperty = DependencyProperty.RegisterAttached(
        "StaggerDeadline", typeof(long), typeof(Anim), new PropertyMetadata(0L));

    private static void OnStaggerChildrenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl || e.NewValue is not true)
        {
            return;
        }

        void OpenRevealWindow() => itemsControl.SetValue(
            StaggerDeadlineProperty, DateTime.UtcNow.AddMilliseconds(900).Ticks);

        OpenRevealWindow();
        itemsControl.Loaded += (_, _) => StaggerNewContainers(itemsControl);
        itemsControl.ItemContainerGenerator.StatusChanged += (_, _) => StaggerNewContainers(itemsControl);
        itemsControl.ItemContainerGenerator.ItemsChanged += (_, args) =>
        {
            // Fresh content (new scan/analysis or initial population) re-opens the
            // reveal window; containers realized by scrolling do not.
            if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                or System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                OpenRevealWindow();
            }
        };
    }

    private static void StaggerNewContainers(ItemsControl itemsControl)
    {
        if (itemsControl.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        bool animate = DateTime.UtcNow.Ticks <= (long)itemsControl.GetValue(StaggerDeadlineProperty);

        int batchIndex = 0;
        for (int i = 0; i < itemsControl.Items.Count; i++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            if ((bool)container.GetValue(StaggeredProperty))
            {
                continue;
            }

            container.SetValue(StaggeredProperty, true);
            if (!animate)
            {
                continue;
            }

            double delay = Math.Min(batchIndex * 38, 460);
            batchIndex++;

            container.Opacity = 0;
            TranslateTransform translate = EnsureTranslate(container);

            var fade = AtDisplayRate(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            var slide = AtDisplayRate(new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            });

            container.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    // =========================================================================
    // AnimateValue — RangeBase (ProgressBar) ramps from 0 to its bound value on load.
    // =========================================================================

    public static readonly DependencyProperty AnimateValueProperty = DependencyProperty.RegisterAttached(
        "AnimateValue", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnAnimateValueChanged));

    public static void SetAnimateValue(DependencyObject element, bool value) => element.SetValue(AnimateValueProperty, value);

    public static bool GetAnimateValue(DependencyObject element) => (bool)element.GetValue(AnimateValueProperty);

    private static void OnAnimateValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase rangeBase || e.NewValue is not true)
        {
            return;
        }

        rangeBase.Loaded += (_, _) =>
        {
            double target = rangeBase.Value;
            if (target <= 0)
            {
                return;
            }

            // FillBehavior.Stop hands control back to the binding when done.
            var ramp = AtDisplayRate(new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(750))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
            rangeBase.BeginAnimation(RangeBase.ValueProperty, ramp);
        };
    }

    // =========================================================================
    // SmoothScroll — animated, inertial mouse-wheel scrolling for ScrollViewers.
    // Apply to a ScrollViewer directly, or to any control (its inner ScrollViewer
    // is located on Loaded — e.g. a ListBox).
    // =========================================================================

    public static readonly DependencyProperty SmoothScrollProperty = DependencyProperty.RegisterAttached(
        "SmoothScroll", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnSmoothScrollChanged));

    public static void SetSmoothScroll(DependencyObject element, bool value) => element.SetValue(SmoothScrollProperty, value);

    public static bool GetSmoothScroll(DependencyObject element) => (bool)element.GetValue(SmoothScrollProperty);

    /// <summary>Pending animated scroll destination (NaN = idle).</summary>
    private static readonly DependencyProperty ScrollTargetProperty = DependencyProperty.RegisterAttached(
        "ScrollTarget", typeof(double), typeof(Anim), new PropertyMetadata(double.NaN));

    /// <summary>Animatable proxy that forwards to ScrollToVerticalOffset.</summary>
    private static readonly DependencyProperty SmoothOffsetProperty = DependencyProperty.RegisterAttached(
        "SmoothOffset", typeof(double), typeof(Anim), new PropertyMetadata(0d, (d, e) =>
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }));

    private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        switch (d)
        {
            case ScrollViewer scrollViewer:
                AttachSmoothScroll(scrollViewer);
                break;
            case FrameworkElement element:
                element.Loaded += (_, _) =>
                {
                    if (FindDescendantScrollViewer(element) is { } inner)
                    {
                        AttachSmoothScroll(inner);
                    }
                };
                break;
        }
    }

    private static void AttachSmoothScroll(ScrollViewer scrollViewer)
    {
        scrollViewer.PreviewMouseWheel += (sender, e) =>
        {
            var sv = (ScrollViewer)sender;
            if (sv.ScrollableHeight <= 0 || e.Delta == 0)
            {
                return;
            }

            e.Handled = true;

            double current = (double)sv.GetValue(ScrollTargetProperty);
            if (double.IsNaN(current))
            {
                current = sv.VerticalOffset;
            }

            double target = Math.Clamp(current - e.Delta, 0, sv.ScrollableHeight);
            sv.SetValue(ScrollTargetProperty, target);

            var glide = AtDisplayRate(new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            glide.Completed += (_, _) => sv.SetValue(ScrollTargetProperty, double.NaN);
            sv.BeginAnimation(SmoothOffsetProperty, glide);
        };
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            if (FindDescendantScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
