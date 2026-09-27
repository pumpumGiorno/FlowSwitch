using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Pages;

public partial class SolarSystemPage : UserControl
{
    public SolarSystemPage() => InitializeComponent();

    private void OnPreviewCount(object sender, RoutedEventArgs e)
    {
        // Only changes the preview; not a setting.
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string text } && int.TryParse(text, out int count) && Preview is not null)
            Preview.WindowCount = count;
    }
}
