using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Pages;

public partial class AnimationsPage : UserControl
{
    public AnimationsPage() => InitializeComponent();

    private void OnReplay(object sender, RoutedEventArgs e) => Preview.Replay();
}
