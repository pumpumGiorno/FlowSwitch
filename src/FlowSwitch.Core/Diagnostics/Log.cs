using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Diagnostics;

public interface ILogSink
{
    void Write(LogLevel level, string message);
}

/// <summary>
/// Minimal static logging facade. The Windows host plugs in an asynchronous file sink; tests and
/// tools leave it silent. Logging must never throw or block the input/render threads.
/// </summary>
public static class Log
{
    private static volatile ILogSink? _sink;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void SetSink(ILogSink? sink) => _sink = sink;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex}");

    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel && _sink is not null;

    public static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        try
        {
            _sink?.Write(level, message);
        }
        catch
        {
            // Never let diagnostics break the app.
        }
    }
}
