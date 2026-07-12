using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BitBroom.App.Views;

/// <summary>Binds nav RadioButtons (IsChecked) to MainViewModel.SelectedTab.</summary>
public sealed class TabIndexConverter : IValueConverter
{
    public static readonly TabIndexConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int selected && int.TryParse(parameter?.ToString(), out int tab) && selected == tab;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && int.TryParse(parameter?.ToString(), out int tab) ? tab : Binding.DoNothing;
}

/// <summary>Shows the page whose index matches SelectedTab.</summary>
public sealed class TabVisibilityConverter : IValueConverter
{
    public static readonly TabVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int selected && int.TryParse(parameter?.ToString(), out int tab) && selected == tab
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
