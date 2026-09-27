using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Settings.Services;

namespace FlowSwitch.Settings.Pages;

public partial class AdvancedPage : UserControl
{
    public AdvancedPage() => InitializeComponent();

    private SettingsModel Model => ((App)Application.Current).Model;

    private async void OnElevatedClick(object sender, RoutedEventArgs e)
    {
        bool enable = ElevatedToggle.IsChecked == true;
        ElevatedToggle.IsEnabled = false;
        try
        {
            string? exe = FlowSwitchHost.FindExecutable();
            bool ok = exe is not null && await Task.Run(() => enable ? ElevatedTask.Create(exe) : ElevatedTask.Delete());
            if (!ok)
            {
                // Cancelled at the UAC prompt (or failed): put the switch back.
                Model.Update(_ => { });
                if (exe is null)
                    MessageBox.Show(Window.GetWindow(this)!, "FlowSwitch.exe was not found next to the Settings app.", "FlowSwitch",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Model.Update(s => s.Advanced.RunElevated = enable);
            Model.SaveNow();

            if (enable && MessageBox.Show(Window.GetWindow(this)!, "Restart FlowSwitch with administrator rights now?", "FlowSwitch",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await Task.Run(() =>
                {
                    FlowSwitchHost.Send(IpcProtocol.Exit);
                    for (int i = 0; i < 40 && FlowSwitchHost.IsRunning; i++) Thread.Sleep(100);
                    ElevatedTask.RunNow();
                });
            }
        }
        finally
        {
            ElevatedToggle.IsEnabled = true;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(FlowSwitchPaths.LogDirectory);

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) => OpenFolder(Model.Store.Directory);

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        Model.SaveNow();
        var result = await FlowSwitchHost.RestartAsync();
        if (!result.Running) MessageBox.Show(Window.GetWindow(this)!, result.Message, "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!, "Reset every FlowSwitch setting to its default?", "Reset settings",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) Model.ResetToDefaults();
    }

    private static void OpenFolder(string path) => DiagnosticsPage.OpenFolder(path);
}
