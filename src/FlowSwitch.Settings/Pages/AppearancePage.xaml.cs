using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Pages;

public partial class AppearancePage : UserControl
{
    public AppearancePage() => InitializeComponent();

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        // Writing through the text box keeps its binding (and the save) in charge.
        if (sender is FrameworkElement { Tag: string hex }) CustomColor.Text = hex;
    }
}
