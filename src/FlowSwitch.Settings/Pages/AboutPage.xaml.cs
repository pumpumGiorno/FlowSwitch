using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        VersionText.Text = $"Version {version} · .NET {Environment.Version.Major} · Windows {Environment.OSVersion.Version.Build}";
    }

    private void OnShowTour(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowOnboarding();
}
