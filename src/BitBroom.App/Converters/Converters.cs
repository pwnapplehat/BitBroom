using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BitBroom.Core.Engine;
using BitBroom.Core.Hogs;

namespace BitBroom.App.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert)
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is not null;
        if (value is string s)
        {
            visible = !string.IsNullOrWhiteSpace(s);
        }

        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RiskLevel.Safe => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
        RiskLevel.Moderate => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        RiskLevel.Advanced => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HogSeverity.Critical => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        HogSeverity.Notable => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        _ => new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
