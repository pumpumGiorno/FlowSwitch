using FlowSwitch.Core.Settings;

namespace FlowSwitch.Settings.Services;

/// <summary>An entry of a drop-down list: the enum value and its human-readable label.</summary>
public sealed record Option(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Drop-down contents, referenced from XAML with x:Static.</summary>
public static class Options
{
    public static Option[] Monitors { get; } =
    {
        new(MonitorPlacement.ActiveWindow, "Monitor with the active window"),
        new(MonitorPlacement.MouseCursor, "Monitor with the mouse pointer"),
        new(MonitorPlacement.AllMonitors, "All monitors"),
    };

    public static Option[] Grouping { get; } =
    {
        new(GroupingMode.Off, "Never"),
        new(GroupingMode.Auto, "Automatically (3+ windows)"),
        new(GroupingMode.Always, "Always"),
    };

    public static Option[] LogLevels { get; } =
    {
        new(LogLevel.Error, "Errors only"),
        new(LogLevel.Warning, "Warnings"),
        new(LogLevel.Info, "Normal"),
        new(LogLevel.Debug, "Verbose (diagnostics)"),
    };

    public static string[] AccentSwatches { get; } =
    {
        "#5B6CFF", "#7A84FF", "#56D6FF", "#3DDC97", "#FFB86B", "#FF6B9A", "#C08BFF", "#E6EAF2",
    };
}
