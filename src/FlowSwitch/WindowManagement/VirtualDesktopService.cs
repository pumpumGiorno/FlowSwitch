using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
using Microsoft.Win32;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.WindowManagement;

internal sealed record VirtualDesktop(Guid Id, string Name, int Index, bool IsCurrent);

/// <summary>
/// Virtual desktop support built only on documented / stable surfaces:
/// <list type="bullet">
/// <item><c>IVirtualDesktopManager</c> (documented COM) tells which desktop a window is on;</item>
/// <item>the desktop list and names come from the Explorer registry keys;</item>
/// <item>switching uses the standard Ctrl+Win+←/→ shortcut, so it animates exactly like Windows.</item>
/// </list>
/// The undocumented IVirtualDesktopManagerInternal (which changes with every Windows build) is not used.
/// </summary>
internal sealed class VirtualDesktopService : IDisposable
{
    private static readonly Guid CLSID_VirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid IID_IVirtualDesktopManager = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private nint _manager;

    public VirtualDesktopService()
    {
        try
        {
            int hr = ComInterop.CoCreateInstance(CLSID_VirtualDesktopManager, 0, ComInterop.CLSCTX_ALL, IID_IVirtualDesktopManager, out _manager);
            if (hr < 0) _manager = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"IVirtualDesktopManager unavailable: {ex.Message}");
        }
    }

    public bool IsAvailable => _manager != 0;

    /// <summary>True if the window is on the current desktop (true when unknown).</summary>
    public unsafe bool IsOnCurrentDesktop(nint hwnd)
    {
        if (_manager == 0) return true;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int*, int>)ComInterop.Slot(_manager, 3);
        int result;
        return fn(_manager, hwnd, &result) < 0 || result != 0;
    }

    public unsafe Guid GetDesktopId(nint hwnd)
    {
        if (_manager == 0) return Guid.Empty;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)ComInterop.Slot(_manager, 4);
        Guid id;
        return fn(_manager, hwnd, &id) >= 0 ? id : Guid.Empty;
    }

    public IReadOnlyList<VirtualDesktop> GetDesktops()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length < 16) return Array.Empty<VirtualDesktop>();

            Guid current = ReadCurrentDesktop(key);
            var list = new List<VirtualDesktop>(ids.Length / 16);
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                string name = $"Desktop {i / 16 + 1}";
                using (var desktopKey = key.OpenSubKey($@"Desktops\{id:B}"))
                {
                    if (desktopKey?.GetValue("Name") is string custom && !string.IsNullOrWhiteSpace(custom)) name = custom;
                }
                list.Add(new VirtualDesktop(id, name, i / 16, id == current));
            }
            if (list.Count > 0 && !list.Any(d => d.IsCurrent)) list[0] = list[0] with { IsCurrent = true };
            return list;
        }
        catch (Exception ex)
        {
            Log.Debug($"Reading virtual desktops failed: {ex.Message}");
            return Array.Empty<VirtualDesktop>();
        }
    }

    private static Guid ReadCurrentDesktop(RegistryKey key)
    {
        if (key.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } cur) return new Guid(cur);
        // Windows 10 keeps it per logon session.
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            using var sessionKey = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
            if (sessionKey?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } s) return new Guid(s);
        }
        catch
        {
            // ignore
        }
        return Guid.Empty;
    }

    /// <summary>Switches to desktop <paramref name="target"/> with the standard shortcut (keeps Windows' animation).</summary>
    public void SwitchTo(int currentIndex, int target)
    {
        int steps = target - currentIndex;
        if (steps == 0) return;
        ushort arrow = (ushort)(steps > 0 ? VK_RIGHT : VK_LEFT);
        int count = Math.Abs(steps);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < count; i++)
            {
                SendDesktopShortcut(arrow);
                await Task.Delay(60).ConfigureAwait(false);
            }
        });
    }

    private static unsafe void SendDesktopShortcut(ushort arrow)
    {
        INPUT* inputs = stackalloc INPUT[6];
        inputs[0] = INPUT.Key(VK_LCONTROL, false, Input.HookBridge.InjectionMarker);
        inputs[1] = INPUT.Key(VK_LWIN, false, Input.HookBridge.InjectionMarker, extended: true);
        inputs[2] = INPUT.Key(arrow, false, Input.HookBridge.InjectionMarker, extended: true);
        inputs[3] = INPUT.Key(arrow, true, Input.HookBridge.InjectionMarker, extended: true);
        inputs[4] = INPUT.Key(VK_LWIN, true, Input.HookBridge.InjectionMarker, extended: true);
        inputs[5] = INPUT.Key(VK_LCONTROL, true, Input.HookBridge.InjectionMarker);
        SendInput(6, inputs, sizeof(INPUT));
    }

    public void Dispose()
    {
        ComInterop.Release(_manager);
        _manager = 0;
    }
}
