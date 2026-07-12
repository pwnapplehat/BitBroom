using System.Windows.Controls;

namespace BitBroom.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
    }

    private void OnConsoleTextChanged(object sender, TextChangedEventArgs e)
        => ConsoleScroll.ScrollToEnd();
}
