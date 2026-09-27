using System.Windows;
using System.Windows.Controls;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Settings.Pages;

public partial class AppearancePage : UserControl
{
    public AppearancePage()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSizePreset();
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        // Writing through the text box keeps its binding (and the save) in charge.
        if (sender is FrameworkElement { Tag: string hex }) CustomColor.Text = hex;
    }

    private void OnSizePreset(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse<CardSizePreset>(name, out var preset)) return;
        // Set through the sliders: their bindings update the settings, and the value change saves them.
        var (selected, orbit) = CardSizePresets.Values(preset);
        SelectedSizeSlider.Value = selected;
        OrbitSizeSlider.Value = orbit;
        UpdateSizePreset();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSizePreset();

    /// <summary>Highlights the preset matching the sliders; none when they are set by hand (Custom).</summary>
    private void UpdateSizePreset()
    {
        if (SizePresets is null || SelectedSizeSlider is null || OrbitSizeSlider is null) return;
        var match = CardSizePresets.Match((float)SelectedSizeSlider.Value, (float)OrbitSizeSlider.Value);
        foreach (var child in SizePresets.Children)
        {
            if (child is RadioButton { Tag: string name } button)
                button.IsChecked = match != CardSizePreset.Custom && name == match.ToString();
        }
    }
}
