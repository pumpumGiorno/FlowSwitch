using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Shell;

/// <summary>Thin base class for raw Win32 windows with an instance message handler.</summary>
internal abstract unsafe class NativeWindow : IDisposable
{
    private static readonly ConcurrentDictionary<nint, NativeWindow> Windows = new();
    private static readonly HashSet<string> RegisteredClasses = new();
    [ThreadStatic] private static NativeWindow? t_creating;

    public nint Handle { get; private set; }

    protected void CreateHandle(string className, uint exStyle, uint style, int x, int y, int width, int height,
        nint parent = 0, nint cursor = 0, uint classStyle = 0)
    {
        RegisterClass(className, cursor, classStyle);
        t_creating = this;
        try
        {
            Handle = CreateWindowExW(exStyle, className, className, style, x, y, width, height, parent, 0, GetModuleHandleW(null), 0);
        }
        finally
        {
            t_creating = null;
        }
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx({className}) failed: {Marshal.GetLastPInvokeError()}");
        Windows[Handle] = this;
    }

    private static void RegisterClass(string className, nint cursor, uint classStyle)
    {
        lock (RegisteredClasses)
        {
            if (!RegisteredClasses.Add(className)) return;
            delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> proc = &StaticWndProc;
            nint name = Marshal.StringToHGlobalUni(className); // lives for the process lifetime
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = classStyle,
                lpfnWndProc = (nint)proc,
                hInstance = GetModuleHandleW(null),
                hCursor = cursor != 0 ? cursor : LoadCursorW(0, IDC_ARROW),
                lpszClassName = name,
            };
            if (RegisterClassExW(wc) == 0) Log.Warn($"RegisterClassEx({className}) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint StaticWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (!Windows.TryGetValue(hwnd, out var window))
            {
                window = t_creating;
                if (window is not null && window.Handle == 0) window.Handle = hwnd;
            }
            if (window is not null)
            {
                nint result = window.WndProc(msg, wParam, lParam, out bool handled);
                if (handled) return result;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"WndProc 0x{msg:X}", ex);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Handles a message. Set <paramref name="handled"/> to skip DefWindowProc.</summary>
    protected abstract nint WndProc(uint msg, nuint wParam, nint lParam, out bool handled);

    public virtual void Dispose()
    {
        if (Handle == 0) return;
        Windows.TryRemove(Handle, out _);
        DestroyWindow(Handle);
        Handle = 0;
    }
}
