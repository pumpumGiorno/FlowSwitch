using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Controls;

/// <summary>One row of settings: icon, title, description on the left, the control on the right.</summary>
public sealed class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(SettingCard), new PropertyMetadata(new CornerRadius(10)));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
