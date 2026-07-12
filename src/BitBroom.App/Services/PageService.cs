using System.Windows;
using Wpf.Ui.Abstractions;

namespace BitBroom.App.Services;

/// <summary>
/// Page provider for the NavigationView. Views are created once and cached (scroll
/// positions, expanded tree nodes and in-flight scans survive tab switches), and each
/// gets the shell view model as DataContext — the navigation host is a Frame, which
/// does not inherit DataContext from the window.
/// </summary>
public sealed class PageService : INavigationViewPageProvider
{
    private readonly object _dataContext;
    private readonly Dictionary<Type, FrameworkElement> _cache = [];

    public PageService(object dataContext)
    {
        _dataContext = dataContext;
    }

    public object? GetPage(Type pageType)
    {
        if (!_cache.TryGetValue(pageType, out FrameworkElement? page))
        {
            page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            page.DataContext = _dataContext;
            _cache[pageType] = page;
        }

        return page;
    }
}
