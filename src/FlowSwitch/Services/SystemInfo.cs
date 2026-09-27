using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Settings;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Services;

internal sealed record MonitorDescriptor(nint Handle, RECT Bounds, RECT WorkArea, string DeviceName, int RefreshRate, bool IsPrimary);

/// <summary>Monitor queries (bounds, refresh rate) for placing overlays.</summary>
internal static unsafe class Monitors
{
    [ThreadStatic] private static List<nint>? t_handles;

    public static IReadOnlyList<MonitorDescriptor> All()
    {
        t_handles = new List<nint>();
        EnumDisplayMonitors(0, null, &EnumProc, 0);
        var list = t_handles.Select(Describe).Where(m => m is not null).Select(m => m!).ToList();
        t_handles = null;
        return list;
    }

    public static MonitorDescriptor? Describe(nint monitor)
    {
        var info = new MONITORINFOEX { cbSize = (uint)sizeof(MONITORINFOEX) };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        string device = new(info.szDevice);
        var mode = new DEVMODE { dmSize = (ushort)sizeof(DEVMODE) };
        int hz = EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode) ? (int)mode.dmDisplayFrequency : 60;
        if (hz <= 1) hz = 60;
        return new MonitorDescriptor(monitor, info.rcMonitor, info.rcWork, device, hz, (info.dwFlags & 1) != 0);
    }

    public static MonitorDescriptor? ForWindow(nint hwnd) =>
        Describe(MonitorFromWindow(hwnd != 0 ? hwnd : GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY));

    public static MonitorDescriptor? ForCursor()
    {
        GetCursorPos(out var pt);
        return Describe(MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY));
    }

    public static MonitorDescriptor? Primary() => Describe(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(nint monitor, nint hdc, RECT* rect, nint data)
    {
        t_handles?.Add(monitor);
        return 1;
    }
}

internal static unsafe class SystemPreferences
{
    /// <summary>Windows "Animation effects" setting (Settings → Accessibility → Visual effects).</summary>
    public static bool PrefersReducedMotion()
    {
        int enabled = 1;
        return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) && enabled == 0;
    }

    public static PowerState GetPowerState()
    {
        if (!GetSystemPowerStatus(out var status)) return PowerState.AC;
        if (status.SystemStatusFlag == 1) return PowerState.BatterySaver;
        return status.ACLineStatus == 0 ? PowerState.Battery : PowerState.AC;
    }

    /// <summary>A Direct3D exclusive-fullscreen game or presentation is running.</summary>
    public static bool IsFullscreenGameRunning()
    {
        const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        const int QUNS_PRESENTATION_MODE = 4;
        return SHQueryUserNotificationState(out int state) >= 0 &&
               state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    public static string? GetProcessName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? Path.GetFileName(new string(buffer, 0, (int)size)) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
