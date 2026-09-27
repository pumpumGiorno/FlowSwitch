using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Input;

/// <summary>
/// The low-level keyboard hook that takes over Alt+Tab.
/// </summary>
/// <remarks>
/// <para>Fail-safe design — FlowSwitch must never leave the user without a working Alt+Tab:</para>
/// <list type="bullet">
/// <item>The hook lives on its own high-priority thread that does nothing but classify keys and
/// post messages. It never waits on the UI, so it can't time out even if the UI stalls.</item>
/// <item>Alt is never swallowed, so Windows' own view of the modifier is always correct.</item>
/// <item>After swallowing Tab, the UI must acknowledge the session within <see cref="HookBridge.AckTimeoutMs"/>.
/// Otherwise the hook replays the keystroke to Windows and the native switcher appears.</item>
/// <item>Keys are only swallowed while the UI heartbeat is fresh; a stalled UI gets the keyboard
/// taken away from it within ~0.5 s.</item>
/// <item>If the process dies, Windows removes the hook automatically — native Alt+Tab returns.</item>
/// <item>The hook is re-installed periodically and after resume, in case Windows silently dropped it.</item>
/// </list>
/// </remarks>
internal sealed unsafe class KeyboardHook : IDisposable
{
    private const uint WmInjectDummy = WM_APP + 20;
    private const uint WmReinstall = WM_APP + 21;
    private const uint WmReplayNative = WM_APP + 22;
    private const int HeartbeatStaleMs = 700;
    private const uint HealthIntervalMs = 100;
    private const uint ReinstallIntervalMs = 60_000;

    private static KeyboardHook? s_current;

    private readonly HookBridge _bridge;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly bool[] _swallowed = new bool[256];
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private bool _prewarmSent;
    private int _pendingSession;
    private bool _pendingReverse;
    private nuint _ackTimer;
    private nuint _healthTimer;
    private nuint _reinstallTimer;

    public KeyboardHook(HookBridge bridge) => _bridge = bridge;

    public bool IsInstalled => _hook != 0;

    public void Start()
    {
        if (_thread is not null) return;
        s_current = this;
        _thread = new Thread(ThreadMain)
        {
            Name = "FlowSwitch.KeyboardHook",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) Log.Warn("Keyboard hook thread did not start in time.");
    }

    /// <summary>Re-installs the hook (after resume / session unlock) — safe to call from any thread.</summary>
    public void RequestReinstall()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WmReinstall, 0, 0);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        s_current = null;
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        // Make sure the thread has a message queue before anyone posts to it.
        PeekMessageW(out _, 0, WM_USER, WM_USER, 0);
        Install();
        _ready.Set();
        _reinstallTimer = SetTimer(0, 0, ReinstallIntervalMs, 0);

        while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            try
            {
                switch (msg.message)
                {
                    case WM_TIMER:
                        OnTimer(msg.wParam);
                        break;
                    case WmInjectDummy:
                        InjectDummyKey();
                        break;
                    case WmReinstall:
                        if (!_bridge.IsSessionActive) Reinstall();
                        break;
                    case WmReplayNative:
                        ReplayToNative(reverse: false);
                        break;
                    default:
                        TranslateMessage(msg);
                        DispatchMessageW(msg);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Keyboard hook loop", ex);
            }
        }

        Uninstall();
    }

    private void Install()
    {
        delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc = &HookProc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, (nint)proc, GetModuleHandleW(null), 0);
        if (_hook == 0) Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
        else Log.Debug("Keyboard hook installed.");
    }

    private void Uninstall()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    private void Reinstall()
    {
        Uninstall();
        Install();
    }

    // ───────────────────────────────── hook procedure ─────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        var self = s_current;
        if (nCode == HC_ACTION && self is not null)
        {
            try
            {
                if (self.Process((KBDLLHOOKSTRUCT*)lParam)) return 1;
            }
            catch
            {
                // An exception must never escape into user32; worst case the key passes through.
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key.</summary>
    private bool Process(KBDLLHOOKSTRUCT* k)
    {
        uint vk = k->vkCode;
        bool up = (k->flags & LLKHF_UP) != 0;

        // Our own synthetic input (dummy key, native fallback replay) always goes through.
        if ((k->flags & LLKHF_INJECTED) != 0 && k->dwExtraInfo == HookBridge.InjectionMarker) return false;
        if (vk >= 256) return false;

        // Never leave an orphaned key-up behind: if we ate the key-down, eat the key-up too.
        if (up && _swallowed[vk])
        {
            _swallowed[vk] = false;
            if (!IsAlt(vk)) return true;
        }

        return _bridge.State == HookBridge.StateIdle ? ProcessIdle(vk, up) : ProcessSession(k, vk, up);
    }

    private bool ProcessIdle(uint vk, bool up)
    {
        var b = _bridge;
        if (!b.Enabled || b.TargetWindow == 0) return false;

        if (IsAlt(vk))
        {
            if (!up && !_prewarmSent && b.Prewarm)
            {
                _prewarmSent = PostMessageW(b.TargetWindow, HookMessages.Prewarm, 0, 0);
            }
            else if (up && _prewarmSent && !AltDown(vk, up))
            {
                PostMessageW(b.TargetWindow, HookMessages.PrewarmEnd, 0, 0);
                _prewarmSent = false;
            }
            return false;
        }

        if (up) return false;
        bool isTab = vk == VK_TAB;
        bool isBacktick = vk == VK_OEM_3;
        if (!isTab && !isBacktick) return false;
        if (!AltDown(vk, up) || WinDown()) return false;
        if (isTab && !b.AltTab) return false;
        if (isBacktick && !b.SameApp) return false;

        // Native Alt+Tab keeps working when FlowSwitch can't (or shouldn't) take over.
        if (b.PassthroughForeground || !b.UiHealthy) return false;

        bool ctrl = CtrlDown();
        bool sticky = false;
        if (ctrl)
        {
            if (!isTab || !b.Sticky) return false;
            sticky = true;
        }

        bool shift = ShiftDown();
        if (shift && isTab && !b.Reverse) return false;

        var flags = BeginFlags.None;
        if (shift) flags |= BeginFlags.Reverse;
        if (sticky) flags |= BeginFlags.Sticky;
        if (isBacktick) flags |= BeginFlags.SameApp;

        int id = b.BeginSession(sticky);
        if (!PostMessageW(b.TargetWindow, HookMessages.Begin, (nuint)flags, id))
        {
            b.EndSession(id);
            return false;
        }

        _pendingSession = id;
        _pendingReverse = shift;
        _prewarmSent = false;
        _swallowed[vk] = true;
        ArmTimers();

        // Alt-down followed only by Alt-up would open the menu bar of the foreground app.
        // A harmless unassigned key in between prevents that (the same trick PowerToys uses).
        PostThreadMessageW(_threadId, WmInjectDummy, 0, 0);
        return true;
    }

    private bool ProcessSession(KBDLLHOOKSTRUCT* k, uint vk, bool up)
    {
        var b = _bridge;
        int id = b.SessionId;
        bool sticky = b.State == HookBridge.StateSticky;

        // A UI that stopped rendering loses the keyboard at once.
        if (b.MillisecondsSinceHeartbeat > HeartbeatStaleMs)
        {
            Abort(id, "UI heartbeat stale");
            return false;
        }

        if (IsAlt(vk))
        {
            if (up && !sticky && !AltDown(vk, up)) Commit(id);
            return false; // Alt itself always reaches Windows.
        }

        if (!sticky && !AltDown(vk, up))
        {
            // Alt was released where we could not see it (e.g. over an elevated window).
            Commit(id);
            return false;
        }

        if (IsModifier(vk) || vk is VK_LWIN or VK_RWIN) return false;
        if (up) return false;

        if (vk == VK_ESCAPE)
        {
            b.EndSession(id);
            PostMessageW(b.TargetWindow, HookMessages.Cancel, 0, id);
            _swallowed[vk] = true;
            return true;
        }

        var flags = KeyFlags.None;
        if (ShiftDown()) flags |= KeyFlags.Shift;
        if (CtrlDown()) flags |= KeyFlags.Ctrl;
        if (AltDown(vk, up)) flags |= KeyFlags.Alt;
        if ((GetKeyState(VK_CAPITAL) & 1) != 0) flags |= KeyFlags.CapsLock;
        if (_swallowed[vk]) flags |= KeyFlags.Repeat;

        nuint wParam = vk | (k->scanCode << 16) | ((k->flags & LLKHF_EXTENDED) != 0 ? 0x8000u : 0u);
        PostMessageW(b.TargetWindow, HookMessages.Key, wParam, (nint)((uint)flags | ((uint)id << 16)));
        _swallowed[vk] = true;
        return true;
    }

    private void Commit(int id)
    {
        _bridge.EndSession(id);
        PostMessageW(_bridge.TargetWindow, HookMessages.Commit, 0, id);
        StopHealthTimer();
    }

    private void Abort(int id, string reason)
    {
        bool wasActive = _bridge.State == HookBridge.StateActive;
        _bridge.EndSession(id);
        _bridge.MarkUnhealthy();
        PostMessageW(_bridge.TargetWindow, HookMessages.Abort, 0, id);
        StopHealthTimer();
        // The user is still holding Alt: let Windows' own switcher take over from here.
        if (wasActive && IsKeyDown(VK_MENU)) PostThreadMessageW(_threadId, WmReplayNative, 0, 0);
        Log.Warn($"Session {id} aborted by keyboard hook: {reason}.");
    }

    // ───────────────────────────────── timers ─────────────────────────────────

    private void ArmTimers()
    {
        if (_ackTimer != 0) KillTimer(0, _ackTimer);
        _ackTimer = SetTimer(0, 0, (uint)Math.Clamp(_bridge.AckTimeoutMs, 60, 1000), 0);
        if (_healthTimer == 0) _healthTimer = SetTimer(0, 0, HealthIntervalMs, 0);
    }

    private void StopHealthTimer()
    {
        if (_healthTimer != 0) KillTimer(0, _healthTimer);
        _healthTimer = 0;
    }

    private void OnTimer(nuint id)
    {
        var b = _bridge;
        if (id == _ackTimer)
        {
            KillTimer(0, _ackTimer);
            _ackTimer = 0;
            if (!b.IsAcknowledged(_pendingSession) && b.SessionId == _pendingSession && b.IsSessionActive)
            {
                // The overlay did not respond in time: hand this Alt+Tab to Windows.
                b.EndSession(_pendingSession);
                b.MarkUnhealthy();
                PostMessageW(b.TargetWindow, HookMessages.Abort, 0, _pendingSession);
                StopHealthTimer();
                ReplayToNative(_pendingReverse);
                Log.Warn($"Overlay did not acknowledge session {_pendingSession} within {b.AckTimeoutMs} ms — used native Alt+Tab.");
            }
        }
        else if (id == _healthTimer)
        {
            if (!b.IsSessionActive)
            {
                StopHealthTimer();
                return;
            }
            if (b.MillisecondsSinceHeartbeat > HeartbeatStaleMs)
            {
                Abort(b.SessionId, "UI heartbeat stale");
            }
            else if (b.State == HookBridge.StateActive && !IsKeyDown(VK_MENU))
            {
                // Missed the Alt-up (e.g. it happened on the secure desktop). Finish the switch.
                Commit(b.SessionId);
            }
        }
        else if (id == _reinstallTimer)
        {
            if (!b.IsSessionActive) Reinstall();
            // While the UI is marked unhealthy, native Alt+Tab is used; a ping lets it prove it recovered.
            if (!b.UiHealthy && b.TargetWindow != 0) PostMessageW(b.TargetWindow, HookMessages.Ping, 0, 0);
        }
    }

    // ───────────────────────────────── injection ─────────────────────────────────

    private static void InjectDummyKey()
    {
        INPUT* inputs = stackalloc INPUT[2];
        inputs[0] = INPUT.Key(VK_DUMMY, up: false, HookBridge.InjectionMarker);
        inputs[1] = INPUT.Key(VK_DUMMY, up: true, HookBridge.InjectionMarker);
        SendInput(2, inputs, sizeof(INPUT));
    }

    /// <summary>Re-sends the swallowed Tab so Windows' own switcher handles this Alt+Tab.</summary>
    private static void ReplayToNative(bool reverse)
    {
        INPUT* inputs = stackalloc INPUT[8];
        uint n = 0;
        bool altHeld = IsKeyDown(VK_MENU);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, false, HookBridge.InjectionMarker);
        if (reverse && !IsKeyDown(VK_SHIFT)) inputs[n++] = INPUT.Key(VK_LSHIFT, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_TAB, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_TAB, true, HookBridge.InjectionMarker);
        if (reverse && !IsKeyDown(VK_SHIFT)) inputs[n++] = INPUT.Key(VK_LSHIFT, true, HookBridge.InjectionMarker);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, true, HookBridge.InjectionMarker);
        SendInput(n, inputs, sizeof(INPUT));
    }

    // ───────────────────────────────── key state ─────────────────────────────────

    private static bool IsAlt(uint vk) => vk is VK_LMENU or VK_RMENU or VK_MENU;

    private static bool IsModifier(uint vk) =>
        vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT or VK_LCONTROL or VK_RCONTROL or VK_CONTROL or VK_CAPITAL;

    /// <summary>
    /// Inside a low-level hook the async key state is not yet updated for the key being
    /// processed, so the current event is taken into account explicitly.
    /// </summary>
    private static bool AltDown(uint vk, bool up) => vk switch
    {
        VK_LMENU => !up || IsKeyDown(VK_RMENU),
        VK_RMENU => !up || IsKeyDown(VK_LMENU),
        VK_MENU => !up,
        _ => IsKeyDown(VK_MENU),
    };

    private static bool ShiftDown() => IsKeyDown(VK_SHIFT);
    private static bool CtrlDown() => IsKeyDown(VK_CONTROL);
    private static bool WinDown() => IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
}
