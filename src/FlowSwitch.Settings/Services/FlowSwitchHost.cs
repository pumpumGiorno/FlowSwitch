using System.Diagnostics;
using System.IO;
using FlowSwitch.Core.Ipc;

namespace FlowSwitch.Settings.Services;

/// <summary>Talks to the resident FlowSwitch process (finds its hidden window, posts commands, starts it).</summary>
public static class FlowSwitchHost
{
    private static uint _message;

    public static bool IsRunning => Find() != 0;

    public static bool Send(int command)
    {
        nint hwnd = Find();
        if (hwnd == 0) return false;
        if (_message == 0) _message = NativeMethods.RegisterWindowMessageW(IpcProtocol.CommandMessage);
        return NativeMethods.PostMessageW(hwnd, _message, command, 0);
    }

    /// <summary>Opens the switcher in preview mode; starts FlowSwitch first if needed.</summary>
    public static bool ShowPreview()
    {
        if (Send(IpcProtocol.ShowPreview)) return true;
        if (!Start()) return false;
        // The resident process needs a moment to create its window; retry briefly in the background.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (Send(IpcProtocol.ShowPreview)) return;
            }
        });
        return true;
    }

    public static bool Start(string? arguments = null)
    {
        string? exe = FindExecutable();
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments ?? string.Empty)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindExecutable()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "FlowSwitch.exe");
        if (File.Exists(local)) return local;

        // Development layout: src/FlowSwitch.Settings/bin/<cfg>/<tfm>/ → src/FlowSwitch/bin/<cfg>/<tfm>/
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string? configuration = dir.Parent?.Name;
            var src = dir.Parent?.Parent?.Parent?.Parent;
            if (src is null || configuration is null) return null;
            string bin = Path.Combine(src.FullName, "FlowSwitch", "bin", configuration);
            if (!Directory.Exists(bin)) return null;
            return Directory.GetFiles(bin, "FlowSwitch.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static nint Find() => NativeMethods.FindWindowW(IpcProtocol.HostWindowClass, null);
}
