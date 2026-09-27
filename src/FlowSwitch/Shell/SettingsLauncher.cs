using System.Diagnostics;
using FlowSwitch.Core.Diagnostics;

namespace FlowSwitch.Shell;

/// <summary>Starts the separate FlowSwitch Settings app (which is single-instance itself).</summary>
internal static class SettingsLauncher
{
    public static void Launch(string? arguments = null)
    {
        string? exe = Find();
        if (exe is null)
        {
            Log.Warn("FlowSwitch.Settings.exe not found next to FlowSwitch.exe.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments ?? string.Empty) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        catch (Exception ex)
        {
            Log.Error("Could not start Settings", ex);
        }
    }

    private static string? Find()
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "FlowSwitch.Settings.exe");
        if (File.Exists(local)) return local;

        // Development layout: src/FlowSwitch/bin/<cfg>/<tfm>/ → src/FlowSwitch.Settings/bin/<cfg>/<tfm>/
        try
        {
            var dir = new DirectoryInfo(baseDir);
            string? configuration = dir.Parent?.Name;
            var src = dir.Parent?.Parent?.Parent?.Parent;
            if (src is not null && configuration is not null)
            {
                var candidates = Directory.Exists(Path.Combine(src.FullName, "FlowSwitch.Settings", "bin", configuration))
                    ? Directory.GetFiles(Path.Combine(src.FullName, "FlowSwitch.Settings", "bin", configuration), "FlowSwitch.Settings.exe", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
