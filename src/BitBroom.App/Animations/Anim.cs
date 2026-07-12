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
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        switch (mode)
        {
            case TransitionMode.SlideFade:
            {
                TranslateTransform translate = EnsureTranslate(element);
                var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(340))
                {
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
                };
                translate.BeginAnimation(TranslateTransform.YProperty, slide);
                break;
            }

            case TransitionMode.ZoomFade:
            {
                ScaleTransform scale = EnsureScale(element);
                var zoom = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
                };
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

    private static void OnStaggerChildrenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl || e.NewValue is not true)
        {
            return;
        }

        itemsControl.ItemContainerGenerator.StatusChanged += (_, _) => StaggerNewContainers(itemsControl);
        itemsControl.Loaded += (_, _) => StaggerNewContainers(itemsControl);
    }

    private static void StaggerNewContainers(ItemsControl itemsControl)
    {
        if (itemsControl.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            return;
        }

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
            double delay = Math.Min(batchIndex * 38, 460);
            batchIndex++;

            container.Opacity = 0;
            TranslateTransform translate = EnsureTranslate(container);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            };

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
            var ramp = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(750))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
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

            var glide = new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
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
