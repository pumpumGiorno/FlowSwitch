using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Input;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.WindowManagement;

/// <summary>
/// Brings a window to the foreground reliably despite Windows' foreground lock.
/// </summary>
/// <remarks>
/// The user just pressed Alt, which normally lifts the foreground lock, so the first attempt
/// almost always succeeds. The fallbacks cover the rest: a synthetic key press (makes this process
/// the source of the last input), then SwitchToThisWindow, which is what the shell uses.
/// </remarks>
internal static unsafe class WindowActivator
{
    public static bool Activate(long handle)
    {
        nint hwnd = (nint)handle;
        if (!IsWindow(hwnd)) return false;

        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        if (TryForeground(hwnd)) return true;

        InjectNeutralKey();
        if (TryForeground(hwnd)) return true;

        // Attaching input queues is only safe when the current foreground thread is responsive.
        nint fg = GetForegroundWindow();
        if (fg != 0 && !IsHungAppWindow(fg))
        {
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint self = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true))
            {
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(self, fgThread, false);
                }
                if (IsForeground(hwnd)) return true;
            }
        }

        SwitchToThisWindow(hwnd, true);
        bool ok = IsForeground(hwnd);
        if (!ok) Log.Warn($"Could not activate window 0x{handle:X}.");
        return ok;
    }

    /// <summary>Asks the window to close, exactly like its title-bar close button.</summary>
    public static void Close(long handle) => PostMessageW((nint)handle, WM_SYSCOMMAND, SC_CLOSE, 0);

    private static bool TryForeground(nint hwnd)
    {
        SetForegroundWindow(hwnd);
        return IsForeground(hwnd);
    }

    private static bool IsForeground(nint hwnd)
    {
        nint fg = GetForegroundWindow();
        return fg == hwnd || (fg != 0 && GetAncestor(fg, GA_ROOTOWNER) == hwnd);
    }

    /// <summary>
    /// Windows lifts the foreground lock when Alt is pressed. Alt is tapped together with an
    /// unassigned key so the previously focused app does not open its menu bar.
    /// </summary>
    private static void InjectNeutralKey()
    {
        INPUT* inputs = stackalloc INPUT[4];
        uint n = 0;
        bool altHeld = IsKeyDown(VK_MENU);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_DUMMY, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_DUMMY, true, HookBridge.InjectionMarker);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, true, HookBridge.InjectionMarker);
        SendInput(n, inputs, sizeof(INPUT));
    }
}
