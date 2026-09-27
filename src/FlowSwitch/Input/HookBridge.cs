using System.Diagnostics;
using FlowSwitch.Interop;

namespace FlowSwitch.Input;

/// <summary>Flags carried by <see cref="HookMessages.Begin"/>.</summary>
[Flags]
internal enum BeginFlags : uint
{
    None = 0,
    Reverse = 1,
    Sticky = 2,
    SameApp = 4,
}

/// <summary>Modifier/state bits carried by <see cref="HookMessages.Key"/> in lParam.</summary>
[Flags]
internal enum KeyFlags : uint
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Up = 8,
    Repeat = 16,
    CapsLock = 32,
}

/// <summary>Messages posted from the hook thread to the switcher thread's message window.</summary>
internal static class HookMessages
{
    /// <summary>Alt+Tab (or variant) pressed while idle. wParam = <see cref="BeginFlags"/>, lParam = session id.</summary>
    public const uint Begin = Win32.WM_APP + 1;
    /// <summary>Key during a session. wParam = vk | scan &lt;&lt; 16, lParam = <see cref="KeyFlags"/> | session id &lt;&lt; 16.</summary>
    public const uint Key = Win32.WM_APP + 2;
    /// <summary>Alt released: activate the selection. lParam = session id.</summary>
    public const uint Commit = Win32.WM_APP + 3;
    /// <summary>Escape: close without switching. lParam = session id.</summary>
    public const uint Cancel = Win32.WM_APP + 4;
    /// <summary>The hook gave up on this session (UI too slow or hung). Hide immediately. lParam = session id.</summary>
    public const uint Abort = Win32.WM_APP + 5;
    /// <summary>Alt went down while idle: warm up captures so Tab finds everything ready.</summary>
    public const uint Prewarm = Win32.WM_APP + 6;
    /// <summary>Alt went up without a session.</summary>
    public const uint PrewarmEnd = Win32.WM_APP + 7;
    /// <summary>Health probe from the hook thread; the UI answers by calling <see cref="HookBridge.ReportAlive"/>.</summary>
    public const uint Ping = Win32.WM_APP + 8;
}

/// <summary>
/// Lock-free state shared between the keyboard-hook thread and the switcher (UI) thread.
/// </summary>
/// <remarks>
/// The hook thread must never block, so all coordination is done with volatile fields and
/// Interlocked operations. The switcher thread proves it is alive through a heartbeat; the hook
/// only swallows keys while that heartbeat is fresh.
/// </remarks>
internal sealed class HookBridge
{
    /// <summary>Marker placed in dwExtraInfo of every input FlowSwitch injects, so the hook lets it through.</summary>
    public const nuint InjectionMarker = 0x464C4F57; // "FLOW"

    public const int StateIdle = 0;
    public const int StateActive = 1;
    public const int StateSticky = 2;

    private volatile int _state;
    private volatile int _sessionId;
    private volatile int _ackedSessionId;
    private long _heartbeat;
    private volatile bool _uiHealthy = true;

    /// <summary>Message window on the switcher thread that receives <see cref="HookMessages"/>.</summary>
    public volatile nint TargetWindow;

    public volatile bool Enabled = true;
    public volatile bool AltTab = true;
    public volatile bool Reverse = true;
    public volatile bool Sticky = true;
    public volatile bool SameApp = true;
    public volatile bool Prewarm = true;
    /// <summary>Set by the switcher when the foreground app should keep native Alt+Tab (fullscreen game, excluded process).</summary>
    public volatile bool PassthroughForeground;
    /// <summary>Milliseconds the UI has to acknowledge a session before native Alt+Tab takes over.</summary>
    public volatile int AckTimeoutMs = 150;

    public int State => _state;
    public int SessionId => _sessionId;
    public bool UiHealthy => _uiHealthy;
    public bool IsSessionActive => _state != StateIdle;

    public static long Now => Stopwatch.GetTimestamp();
    public static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;

    /// <summary>Hook thread: opens a new session and returns its id.</summary>
    public int BeginSession(bool sticky)
    {
        // Only the hook thread opens sessions, so a plain increment is safe. Ids fit in 15 bits
        // because they travel in the upper half of a message lParam.
        int id = (_sessionId + 1) & 0x7FFF;
        if (id == 0) id = 1;
        _sessionId = id;
        Volatile.Write(ref _heartbeat, Now);
        _state = sticky ? StateSticky : StateActive;
        return id;
    }

    /// <summary>Ends the session (either thread). Keys pass through again immediately.</summary>
    public void EndSession(int sessionId)
    {
        if (sessionId == 0 || sessionId == _sessionId) _state = StateIdle;
    }

    /// <summary>Switcher thread: "I received session <paramref name="sessionId"/> and I'm on it."</summary>
    public void Acknowledge(int sessionId)
    {
        _ackedSessionId = sessionId;
        ReportAlive();
    }

    public bool IsAcknowledged(int sessionId) => _ackedSessionId == sessionId;

    /// <summary>Switcher thread heartbeat (every frame while visible, every message while idle).</summary>
    public void ReportAlive()
    {
        Volatile.Write(ref _heartbeat, Now);
        _uiHealthy = true;
    }

    public long MillisecondsSinceHeartbeat => Ms(Now - Volatile.Read(ref _heartbeat));

    public void MarkUnhealthy() => _uiHealthy = false;
}
