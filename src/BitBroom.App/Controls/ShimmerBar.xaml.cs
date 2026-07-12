using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace BitBroom.App.Controls;

/// <summary>Indeterminate progress shimmer. The sweep runs only while visible.</summary>
public partial class ShimmerBar : UserControl
{
    private readonly DoubleAnimation _sweep;

    public ShimmerBar()
    {
        InitializeComponent();
        _sweep = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                SweepTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, _sweep);
            }
            else
            {
                SweepTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            }
        };
    }
}
