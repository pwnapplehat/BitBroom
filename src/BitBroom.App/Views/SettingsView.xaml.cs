using System.Windows.Controls;

namespace BitBroom.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>00:00 … 23:00 choices for the schedule time ComboBox.</summary>
    public string[] HourChoices { get; } = [.. Enumerable.Range(0, 24).Select(h => $"{h:00}:00")];
}
