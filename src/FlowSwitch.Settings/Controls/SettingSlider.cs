using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FlowSwitch.Settings.Controls;

public enum ValueFormat
{
    Percent,
    Multiplier,
    Milliseconds,
    Number,
    Decimal,
    Units,
}

/// <summary>A slider with its current value printed next to the track.</summary>
public sealed class SettingSlider : Slider
{
    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(ValueFormat), typeof(SettingSlider),
            new PropertyMetadata(ValueFormat.Percent, (d, _) => ((SettingSlider)d).UpdateText()));

    private static readonly DependencyPropertyKey DisplayTextKey =
        DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(SettingSlider), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

    /// <summary>Text shown for the lowest value instead of the number (e.g. "Auto").</summary>
    public static readonly DependencyProperty MinimumLabelProperty =
        DependencyProperty.Register(nameof(MinimumLabel), typeof(string), typeof(SettingSlider),
            new PropertyMetadata(null, (d, _) => ((SettingSlider)d).UpdateText()));

    public SettingSlider()
    {
        Minimum = 0;
        Maximum = 1;
        UpdateText();
    }

    public ValueFormat Format
    {
        get => (ValueFormat)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public string? MinimumLabel
    {
        get => (string?)GetValue(MinimumLabelProperty);
        set => SetValue(MinimumLabelProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateText();
    }

    private void UpdateText()
    {
        double v = Value;
        var c = CultureInfo.CurrentCulture;
        string text = MinimumLabel is { } label && v <= Minimum + 1e-6 ? label : Format switch
        {
            ValueFormat.Percent => Math.Round(v * 100).ToString("0", c) + "%",
            ValueFormat.Multiplier => v.ToString("0.00", c) + "×",
            ValueFormat.Milliseconds => Math.Round(v).ToString("0", c) + " ms",
            ValueFormat.Decimal => v.ToString("0.00", c),
            ValueFormat.Units => Math.Round(v).ToString("0", c) + " px",
            _ => Math.Round(v).ToString("0", c),
        };
        SetValue(DisplayTextKey, text);
    }
}
