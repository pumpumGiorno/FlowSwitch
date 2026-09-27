using FlowSwitch.Core.Diagnostics;
using Microsoft.Win32;

namespace FlowSwitch.Shell;

/// <summary>
/// "Start with Windows" via the per-user Run key: no admin rights, starts after the shell is up,
/// and Windows' Startup Apps page can toggle it. (The elevated variant is a scheduled task
/// created by the Settings app, see Settings → Advanced.)
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FlowSwitch";

    public static void Apply(bool enabled, bool elevatedTaskInUse)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && !elevatedTaskInUse && Environment.ProcessPath is { } exe)
            {
                string command = $"\"{exe}\" --startup";
                if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update startup registration: {ex.Message}");
        }
    }
}
