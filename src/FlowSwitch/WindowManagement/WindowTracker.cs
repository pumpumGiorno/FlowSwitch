using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Model;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.WindowManagement;

/// <summary>
/// Knows every switchable window and their most-recently-used order.
/// </summary>
/// <remarks>
/// Event driven (WinEvent hooks), no polling: FlowSwitch costs nothing while idle. MRU order comes
/// from real foreground activations; windows never activated since FlowSwitch started keep their
/// Z-order position, which is what Windows itself uses. Must be created on the switcher thread,
/// which pumps messages (WinEvent callbacks arrive there).
/// </remarks>
internal sealed unsafe class WindowTracker : IDisposable
{
    private static WindowTracker? s_instance;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
        "Windows.Internal.Shell.TabProxyWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "ApplicationManager_ImmersiveShellWindow", "Internet Explorer_Hidden", "EdgeUiInputTopWndClass",
        "Shell_InputSwitchTopLevelWindow", "SysShadow", "tooltips_class32", "#32768",
    };

    private readonly AppIdentityResolver _identities;
    private readonly VirtualDesktopService _desktops;
    private readonly Dictionary<nint, WindowInfo> _windows = new();
    private readonly Dictionary<nint, long> _stamps = new();
    private readonly HashSet<nint> _pinned = new();
    private readonly List<nint> _hooks = new();
    private readonly uint _ownPid = GetCurrentProcessId();
    private readonly List<nint> _enumBuffer = new(256);
    private long _clock;

    public WindowTracker(AppIdentityResolver identities, VirtualDesktopService desktops)
    {
        _identities = identities;
        _desktops = desktops;
    }

    /// <summary>Raised on the switcher thread when the foreground window changes.</summary>
    public event Action<nint>? ForegroundChanged;

    /// <summary>Raised when windows appear, disappear or change state (coalesce before reacting).</summary>
    public event Action? WindowsChanged;

    /// <summary>Raised when a new app identity is seen (icon / accent should be loaded).</summary>
    public event Action<WindowInfo>? WindowDiscovered;

    public bool IncludeOtherDesktops { get; set; }

    public bool IncludeMinimized { get; set; } = true;

    public HashSet<string> HiddenProcesses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        s_instance = this;
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> proc = &WinEventProc;
        uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        (uint Min, uint Max)[] ranges =
        {
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
            (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        };
        foreach (var (min, max) in ranges)
        {
            nint hook = SetWinEventHook(min, max, 0, (nint)proc, 0, 0, flags);
            if (hook != 0) _hooks.Add(hook);
        }

        // Seed MRU from Z-order: that is Windows' own notion of recency for top-level windows.
        EnumerateZOrder();
        long n = _enumBuffer.Count;
        for (int i = 0; i < _enumBuffer.Count; i++) _stamps[_enumBuffer[i]] = n - i;
        _clock = n + 1;
        StampForeground(GetForegroundWindow());
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (s_instance == this) s_instance = null;
    }

    public WindowInfo? Find(long handle) => _windows.GetValueOrDefault((nint)handle);

    public bool TogglePin(long handle)
    {
        nint h = (nint)handle;
        if (!_pinned.Remove(h)) _pinned.Add(h);
        bool pinned = _pinned.Contains(h);
        if (_windows.TryGetValue(h, out var w)) w.IsPinned = pinned;
        return pinned;
    }

    /// <summary>All switchable windows, most recently used first.</summary>
    public List<WindowInfo> Snapshot()
    {
        EnumerateZOrder();
        var candidates = new List<(WindowInfo Info, long Stamp, int Z)>(_enumBuffer.Count);
        var seen = new HashSet<nint>();
        var pids = new HashSet<uint>();

        for (int z = 0; z < _enumBuffer.Count; z++)
        {
            nint h = _enumBuffer[z];
            if (!IsSwitchable(h, out bool otherDesktop)) continue;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == _ownPid) continue;

            string title = GetWindowText(h);
            if (title.Length == 0) continue;

            if (!_windows.TryGetValue(h, out var info))
            {
                var app = _identities.Resolve(h, pid, GetClassName(h), title);
                info = new WindowInfo { Handle = h, App = app };
                _windows[h] = info;
                WindowDiscovered?.Invoke(info);
            }
            if (HiddenProcesses.Count > 0 && HiddenProcesses.Contains(info.App.ExecutableName)) continue;

            info.Title = title;
            info.IsMinimized = IsIconic(h);
            if (info.IsMinimized && !IncludeMinimized) continue;
            info.IsHung = IsHungAppWindow(h);
            info.IsPinned = _pinned.Contains(h);
            info.IsOnCurrentDesktop = !otherDesktop;
            if (otherDesktop && !IncludeOtherDesktops) continue;
            info.Bounds = GetBounds(h, info.IsMinimized);
            info.LastActivated = _stamps.GetValueOrDefault(h);

            candidates.Add((info, info.LastActivated, z));
            seen.Add(h);
            pids.Add(pid);
        }

        // Forget windows that no longer exist.
        foreach (var h in _windows.Keys.ToList())
        {
            if (seen.Contains(h) || IsWindow(h)) continue;
            _windows.Remove(h);
            _stamps.Remove(h);
            _pinned.Remove(h);
        }
        _identities.Trim(pids);

        candidates.Sort(static (a, b) =>
        {
            int byStamp = b.Stamp.CompareTo(a.Stamp);
            return byStamp != 0 ? byStamp : a.Z.CompareTo(b.Z);
        });
        return candidates.Select(c => c.Info).ToList();
    }

    public int CountOnDesktop(Guid desktop, IReadOnlyList<WindowInfo> windows) =>
        windows.Count(w => _desktops.GetDesktopId((nint)w.Handle) == desktop);

    // ───────────────────────────────── eligibility ─────────────────────────────────

    /// <summary>
    /// The Alt+Tab rules: visible, not a tool window (unless forced onto the taskbar), not owned by
    /// another visible window, not cloaked by the app, not part of the shell.
    /// </summary>
    private bool IsSwitchable(nint h, out bool otherDesktop)
    {
        otherDesktop = false;
        if (!IsWindowVisible(h)) return false;

        uint ex = GetExStyle(h);
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && !appWindow) return false;

        nint owner = GetWindow(h, GW_OWNER);
        if (owner != 0 && IsWindowVisible(owner) && !appWindow) return false;

        int cloak = GetCloakReason(h);
        if (cloak != 0)
        {
            // DWM_CLOAKED_SHELL (2) is how Windows hides windows of other virtual desktops.
            if (cloak != 2) return false;
            if (_desktops.IsOnCurrentDesktop(h) || _desktops.GetDesktopId(h) == Guid.Empty) return false;
            otherDesktop = true;
        }

        string cls = GetClassName(h);
        if (IgnoredClasses.Contains(cls) || cls.StartsWith("FlowSwitch.", StringComparison.Ordinal)) return false;
        return true;
    }

    private static RectI GetBounds(nint h, bool minimized)
    {
        if (minimized)
        {
            var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (GetWindowPlacement(h, ref placement))
            {
                var r = placement.rcNormalPosition;
                return new RectI(r.Left, r.Top, r.Width, r.Height);
            }
        }
        var rect = GetFrameBounds(h);
        return new RectI(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private void EnumerateZOrder()
    {
        _enumBuffer.Clear();
        s_enumTarget = _enumBuffer;
        EnumWindows(&EnumProc, 0);
        s_enumTarget = null;
    }

    [ThreadStatic] private static List<nint>? s_enumTarget;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(nint hwnd, nint lParam)
    {
        s_enumTarget?.Add(hwnd);
        return 1;
    }

    // ───────────────────────────────── WinEvents ─────────────────────────────────

    private void StampForeground(nint hwnd)
    {
        if (hwnd == 0) return;
        long stamp = ++_clock;
        _stamps[hwnd] = stamp;
        nint root = GetAncestor(hwnd, GA_ROOTOWNER);
        if (root != 0 && root != hwnd) _stamps[root] = stamp;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var self = s_instance;
        if (self is null || hwnd == 0 || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        try
        {
            self.OnWinEvent(evt, hwnd);
        }
        catch
        {
            // Never throw into user32.
        }
    }

    private void OnWinEvent(uint evt, nint hwnd)
    {
        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
                StampForeground(hwnd);
                ForegroundChanged?.Invoke(hwnd);
                break;
            case EVENT_OBJECT_NAMECHANGE:
                if (_windows.TryGetValue(hwnd, out var named)) named.Title = GetWindowText(hwnd);
                break;
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                if (_windows.TryGetValue(hwnd, out var min)) min.IsMinimized = evt == EVENT_SYSTEM_MINIMIZESTART;
                WindowsChanged?.Invoke();
                break;
            default:
                // Create / destroy / show / hide / cloak: only top-level windows matter.
                if (_windows.ContainsKey(hwnd) || GetAncestor(hwnd, GA_ROOT) == hwnd) WindowsChanged?.Invoke();
                break;
        }
    }
}
