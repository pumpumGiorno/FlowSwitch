using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Settings.Services;

namespace FlowSwitch.Settings.Pages;

/// <summary>Live health of FlowSwitch.exe, the test overlay, the self-test and the logs.</summary>
public partial class DiagnosticsPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _busy;

    public DiagnosticsPage()
    {
        InitializeComponent();
        LogCard.Description = FlowSwitchPaths.HostLog;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var status = await Task.Run(FlowSwitchHost.QueryStatus);
            Show(status);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(HostStatus? status)
    {
        var (headline, detail, tone) = HostStatusText.Summary(status, FlowSwitchHost.LastStartError);
        Headline.Text = headline;
        Headline.Foreground = Brush(tone, primary: true);
        HeadlineDetail.Text = detail;
        SafeModeCard.Visibility = status is { } s && s.HasFlag(HostStatus.SafeMode) ? Visibility.Visible : Visibility.Collapsed;

        var rows = HostStatusText.Rows(status);
        StatusGrid.Children.Clear();
        StatusGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            var (name, value, rowTone) = rows[i];
            StatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = name, Margin = new Thickness(0, 3, 0, 3), Foreground = (Brush)FindResource("TextSecondaryBrush") };
            var text = new TextBlock { Text = value, Margin = new Thickness(0, 3, 0, 3), FontWeight = FontWeights.SemiBold, Foreground = Brush(rowTone, primary: false) };
            Grid.SetRow(label, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            StatusGrid.Children.Add(label);
            StatusGrid.Children.Add(text);
        }
    }

    private Brush Brush(StatusTone tone, bool primary) => tone switch
    {
        StatusTone.Good => new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)),
        StatusTone.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6B)),
        StatusTone.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        _ => (Brush)FindResource(primary ? "TextPrimaryBrush" : "TextSecondaryBrush"),
    };

    private async void OnTestOverlay(object sender, RoutedEventArgs e)
    {
        TestOverlayButton.IsEnabled = false;
        try
        {
            var result = await FlowSwitchHost.TestOverlayAsync();
            if (!result.Running) MessageBox.Show(Window.GetWindow(this)!, result.Message, "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Warning);
            await Task.Delay(5500);
        }
        finally
        {
            TestOverlayButton.IsEnabled = true;
        }
        Refresh();
    }

    private async void OnRunDiagnostics(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        RunButton.Content = "Running…";
        ReportPanel.Visibility = Visibility.Visible;
        ReportBox.Text = "Running diagnostics… (don't touch the keyboard)";
        try
        {
            ReportBox.Text = await FlowSwitchHost.RunDiagnosticsAsync();
        }
        finally
        {
            RunButton.IsEnabled = true;
            RunButton.Content = "Run diagnostics";
        }
        Refresh();
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnExitSafeMode(object sender, RoutedEventArgs e)
    {
        FlowSwitchHost.Send(IpcProtocol.ExitSafeMode);
        Refresh();
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).Model.SaveNow();
        var result = await FlowSwitchHost.RestartAsync();
        if (!result.Running) MessageBox.Show(Window.GetWindow(this)!, result.Message, "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(FlowSwitchPaths.LogDirectory);

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
