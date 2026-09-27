using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FlowSwitch.Core.Color;

namespace FlowSwitch.Settings.Converters;

/// <summary>Binds a RadioButton to one value of an enum: ConverterParameter is the value's name.</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name) return Binding.DoNothing;
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum ? Enum.Parse(enumType, name) : Binding.DoNothing;
    }
}

/// <summary>Visible when the bound value equals ConverterParameter (enum name or bool).</summary>
public sealed class EqualsVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool equal = value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        return equal ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value equals ConverterParameter (enables controls for one mode only).</summary>
public sealed class EqualsBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A list of process names edited as one name per line.</summary>
public sealed class LinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string> lines ? string.Join(Environment.NewLine, lines) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>"#RRGGBB" → brush (for the ambient colour swatch).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!ColorF.TryParseHex(value as string, out var c)) return Brushes.Transparent;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Clamp(c.R * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.G * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.B * 255f + 0.5f, 0, 255)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
