using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Diagnostics;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Input;

/// <summary>
/// The low-level keyboard hook that takes over Alt+Tab.
/// </summary>
/// <remarks>
/// <para>Fail-safe design — FlowSwitch must never leave the user without a working Alt+Tab:</para>
/// <list type="bullet">
/// <item>Alt+Tab is only swallowed while the switcher has a working renderer and overlay
/// (<see cref="HookBridge.RendererReady"/>). Until then — or after any graphics failure — every key
/// goes to Windows untouched and the native switcher opens as usual.</item>
/// <item>The hook lives on its own high-priority thread that does nothing but classify keys and
/// post messages. It never waits on the UI, so it can't time out even if the UI stalls.</item>
/// <item>Alt is never swallowed, so Windows' own view of the modifier is always correct.</item>
/// <item>After swallowing Tab, the UI must acknowledge the session within <see cref="HookBridge.AckTimeoutMs"/>,
/// or reject it; either way the keystroke is replayed to Windows and the native switcher appears.</item>
/// <item>Keys are only swallowed while the UI heartbeat is fresh; a stalled UI gets the keyboard
/// taken away from it within ~0.5 s.</item>
/// <item>If the process dies, Windows removes the hook automatically — native Alt+Tab returns.</item>
/// <item>The hook is re-installed periodically and after resume, in case Windows silently dropped it.</item>
/// </list>
/// The callback is a static <see cref="UnmanagedCallersOnlyAttribute"/> function, so there is no
/// delegate the garbage collector could ever collect.
/// </remarks>
internal sealed unsafe class KeyboardHook : IDisposable
{
    private const uint WmInjectDummy = WM_APP + 20;
    private const uint WmReinstall = WM_APP + 21;
    private const uint WmReplayNative = WM_APP + 22;
    private const int HeartbeatStaleMs = 700;
    private const uint HealthIntervalMs = 100;
    private const uint ReinstallIntervalMs = 60_000;
    private const uint AltGrControlScanCode = 0x21D;

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
    private bool _firstEventLogged;
    // AltGr arrives as a synthetic LCtrl (scan code 0x21D) + RAlt. That Ctrl is not the user's.
    private bool _altGrControlDown;
    private bool _realLeftControlDown;

    public KeyboardHook(HookBridge bridge) => _bridge = bridge;

    public bool IsInstalled => _hook != 0;

    public void Start()
    {
        if (_thread is not null) return;
        s_current = this;
        HostHealth.Hook = ComponentState.Starting;
        _thread = new Thread(ThreadMain)
        {
            Name = "FlowSwitch.KeyboardHook",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) Log.Error("Keyboard hook thread did not start within 3 s.");
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
        try
        {
            _threadId = GetCurrentThreadId();
            HostHealth.HookThreadId = _threadId;
            _bridge.HookThreadId = _threadId;
            Log.Info($"Keyboard hook thread started (thread id {_threadId}).");
            // Make sure the thread has a message queue before anyone posts to it.
            PeekMessageW(out _, 0, WM_USER, WM_USER, 0);
            Install();
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook thread failed to start", ex);
            HostHealth.Hook = ComponentState.Failed;
            HostHealth.HookError = ErrorText.Of(ex);
        }
        finally
        {
            _ready.Set();
        }
        _reinstallTimer = SetTimer(0, 0, ReinstallIntervalMs, 0);

        // The hook callback runs inside GetMessage on this thread: it must keep pumping for as
        // long as the process lives, and it never exits on an exception.
        int result;
        while ((result = GetMessageW(out MSG msg, 0, 0, 0)) > 0)
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
                        if (!_bridge.IsSessionActive) Reinstall("resume / session unlock");
                        break;
                    case WmReplayNative:
                        ReplayToNative(reverse: false);
                        break;
                    case HookThreadMessages.Reject:
                        OnReject((int)msg.wParam);
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
        if (result < 0) ErrorText.LogWin32Failure("GetMessageW (hook thread)", result, Marshal.GetLastPInvokeError());
        Log.Info("Keyboard hook thread exiting.");
        Uninstall();
    }

    private void Install()
    {
        delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc = &HookProc;
        nint module = GetModuleHandleW(null);
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, (nint)proc, module, 0);
        int error = Marshal.GetLastPInvokeError();
        HostHealth.HookHandle = _hook;
        if (_hook == 0)
        {
            ErrorText.LogWin32Failure("SetWindowsHookEx(WH_KEYBOARD_LL)", "NULL", error);
            HostHealth.Hook = ComponentState.Failed;
            HostHealth.HookError = $"SetWindowsHookEx failed: {ErrorText.Win32(error)}";
        }
        else
        {
            HostHealth.Hook = ComponentState.Ready;
            HostHealth.HookError = null;
            Log.Info($"SetWindowsHookEx(WH_KEYBOARD_LL) succeeded: hook handle 0x{_hook:X}, module 0x{module:X}, thread {_threadId}.");
        }
    }

    private void Uninstall()
    {
        if (_hook == 0) return;
        if (!UnhookWindowsHookEx(_hook)) ErrorText.LogWin32Failure("UnhookWindowsHookEx", false, Marshal.GetLastPInvokeError());
        _hook = 0;
        HostHealth.HookHandle = 0;
    }

    private void Reinstall(string reason)
    {
        Log.Debug($"Re-installing keyboard hook ({reason}).");
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
                if (self.Process((uint)wParam, (KBDLLHOOKSTRUCT*)lParam)) return 1;
            }
            catch
            {
                // An exception must never escape into user32; worst case the key passes through.
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key.</summary>
    private bool Process(uint message, KBDLLHOOKSTRUCT* k)
    {
        uint vk = k->vkCode;
        // WM_KEYUP / WM_SYSKEYUP carry LLKHF_UP; WM_SYSKEY* just means Alt is involved.
        bool up = (k->flags & LLKHF_UP) != 0 || message is WM_KEYUP or WM_SYSKEYUP;
        Interlocked.Increment(ref HostHealth.HookEvents);
        if (!_firstEventLogged)
        {
            _firstEventLogged = true;
            Log.Info("Keyboard hook is receiving input.");
        }

        // Our own synthetic input (dummy key, native fallback replay) always goes through.
        if ((k->flags & LLKHF_INJECTED) != 0 && k->dwExtraInfo == HookBridge.InjectionMarker) return false;
        if (vk >= 256) return false;

        if (vk == VK_LCONTROL)
        {
            if (k->scanCode == AltGrControlScanCode) _altGrControlDown = !up;
            else _realLeftControlDown = !up;
        }
        if (_bridge.VerboseInput && (IsAlt(vk) || _bridge.IsSessionActive || AltDown(k, vk, up))) LogKey(message, k, vk, up);

        // Ctrl+Alt+F12 (test overlay) is normally a RegisterHotKey; the host uses this signal only
        // if that hotkey does not arrive (e.g. the graphics driver's control panel owns it).
        if (!up && vk == VK_F12 && _bridge.HostWindow != 0 && CtrlDown() && AltDown(k, vk, up))
            PostMessageW(_bridge.HostWindow, HookBridge.DiagnosticKeyMessage, 0, 0);

        // Never leave an orphaned key-up behind: if we ate the key-down, eat the key-up too.
        if (up && _swallowed[vk])
        {
            _swallowed[vk] = false;
            if (!IsAlt(vk)) return true;
        }

        return _bridge.State == HookBridge.StateIdle ? ProcessIdle(k, vk, up) : ProcessSession(k, vk, up);
    }

    private static void LogKey(uint message, KBDLLHOOKSTRUCT* k, uint vk, bool up)
    {
        string? name = vk switch
        {
            VK_LMENU => "LEFT ALT",
            VK_RMENU => "RIGHT ALT",
            VK_MENU => "ALT",
            VK_TAB => "TAB",
            VK_LSHIFT or VK_RSHIFT or VK_SHIFT => "SHIFT",
            VK_ESCAPE => "ESC",
            _ => null, // Deliberately nothing else: the log must never become a key logger.
        };
        if (name is null) return;
        if (vk == VK_TAB && !up) Interlocked.Exchange(ref HostHealth.LastTabDownTicks, Environment.TickCount64);
        if (IsAlt(vk) && !up) Interlocked.Exchange(ref HostHealth.LastAltDownTicks, Environment.TickCount64);
        string msg = message switch
        {
            WM_KEYDOWN => "WM_KEYDOWN",
            WM_KEYUP => "WM_KEYUP",
            WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
            WM_SYSKEYUP => "WM_SYSKEYUP",
            _ => $"0x{message:X}",
        };
        bool injected = (k->flags & LLKHF_INJECTED) != 0;
        bool altFlag = (k->flags & LLKHF_ALTDOWN) != 0;
        Log.Info($"{name} {(up ? "UP" : "DOWN")} ({msg}{(altFlag ? ", alt flag" : string.Empty)}{(injected ? ", injected" : string.Empty)})");
    }

    private bool ProcessIdle(KBDLLHOOKSTRUCT* k, uint vk, bool up)
    {
        var b = _bridge;
        if (b.TargetWindow == 0 || !b.Enabled)
        {
            if (!up && vk == VK_TAB && AltDown(k, vk, up)) PassToWindows(b.TargetWindow == 0 ? "switcher thread not running" : "FlowSwitch is off, paused or in safe mode");
            return false;
        }

        if (IsAlt(vk))
        {
            if (!b.RendererReady) return false;
            if (!up && !_prewarmSent && b.Prewarm)
            {
                _prewarmSent = PostMessageW(b.TargetWindow, HookMessages.Prewarm, 0, 0);
            }
            else if (up && _prewarmSent && !AltDown(k, vk, up))
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
        if (!AltDown(k, vk, up) || WinDown()) return false;
        if (isTab && !b.AltTab) return PassToWindows("Alt+Tab is turned off in Settings → Hotkeys");
        if (isBacktick && !b.SameApp) return false;

        // Native Alt+Tab keeps working whenever FlowSwitch can't (or shouldn't) take over.
        if (!b.RendererReady) return PassToWindows("renderer / overlay not ready");
        if (!b.UiHealthy) return PassToWindows("switcher thread was unresponsive");
        if (b.PassthroughForeground) return PassToWindows("foreground app keeps Windows Alt+Tab (fullscreen game or excluded app)");

        bool ctrl = CtrlDown();
        bool sticky = false;
        if (ctrl)
        {
            if (!isTab || !b.Sticky) return false;
            sticky = true;
        }

        bool shift = ShiftDown();
        if (shift && isTab && !b.Reverse) return PassToWindows("Alt+Shift+Tab is turned off in Settings → Hotkeys");

        var flags = BeginFlags.None;
        if (shift) flags |= BeginFlags.Reverse;
        if (sticky) flags |= BeginFlags.Sticky;
        if (isBacktick) flags |= BeginFlags.SameApp;

        int id = b.BeginSession(sticky);
        if (!PostMessageW(b.TargetWindow, HookMessages.Begin, (nuint)flags, id))
        {
            int error = Marshal.GetLastPInvokeError();
            b.EndSession(id);
            ErrorText.LogWin32Failure("PostMessage(Begin)", false, error, "switcher window unreachable — Alt+Tab left to Windows");
            return false;
        }

        _pendingSession = id;
        _pendingReverse = shift;
        _prewarmSent = false;
        _swallowed[vk] = true;
        ArmTimers();
        Interlocked.Increment(ref HostHealth.InterceptedSessions);
        Log.Info($"{(isTab ? "Alt+Tab" : "Alt+`")} intercepted → switcher session {id} requested ({flags}).");

        // Alt-down followed only by Alt-up would open the menu bar of the foreground app.
        // A harmless unassigned key in between prevents that (the same trick PowerToys uses).
        PostThreadMessageW(_threadId, WmInjectDummy, 0, 0);
        return true;
    }

    /// <summary>Logs why an Alt+Tab went to Windows. Always returns false (= let the key through).</summary>
    private static bool PassToWindows(string reason)
    {
        HostHealth.LastPassReason = reason;
        Interlocked.Increment(ref HostHealth.NativeFallbacks);
        Log.Info($"Alt+Tab passed to Windows: {reason}.");
        return false;
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
            if (up && !sticky && !AltDown(k, vk, up))
            {
                Log.Info($"Alt released → commit session {id}.");
                Commit(id);
            }
            return false; // Alt itself always reaches Windows.
        }

        if (!sticky && !AltDown(k, vk, up))
        {
            // Alt was released where we could not see it (e.g. over an elevated window).
            Log.Info($"Alt no longer down (release not seen) → commit session {id}.");
            Commit(id);
            return false;
        }

        if (IsModifier(vk) || vk is VK_LWIN or VK_RWIN) return false;
        if (up) return false;

        if (vk == VK_ESCAPE)
        {
            Log.Info($"Esc → cancel session {id}.");
            b.EndSession(id);
            PostMessageW(b.TargetWindow, HookMessages.Cancel, 0, id);
            StopHealthTimer();
            _swallowed[vk] = true;
            return true;
        }

        var flags = KeyFlags.None;
        if (ShiftDown()) flags |= KeyFlags.Shift;
        if (CtrlDown()) flags |= KeyFlags.Ctrl;
        if (AltDown(k, vk, up)) flags |= KeyFlags.Alt;
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
        Interlocked.Increment(ref HostHealth.NativeFallbacks);
        Log.Warn($"Session {id} aborted by keyboard hook: {reason} — keyboard given back to Windows.");
    }

    /// <summary>The switcher said it cannot show this session: hand the Alt+Tab to Windows.</summary>
    private void OnReject(int id)
    {
        var b = _bridge;
        // The switcher may already have ended the session on its side; only a newer session wins.
        if (b.SessionId != id)
        {
            Log.Debug($"Reject for session {id} ignored (session {b.SessionId} is newer).");
            return;
        }
        b.EndSession(id);
        StopHealthTimer();
        if (_ackTimer != 0)
        {
            KillTimer(0, _ackTimer);
            _ackTimer = 0;
        }
        Interlocked.Increment(ref HostHealth.NativeFallbacks);
        if (IsKeyDown(VK_MENU))
        {
            ReplayToNative(id == _pendingSession && _pendingReverse);
            Log.Warn($"Switcher rejected session {id} — replayed Alt+Tab to Windows.");
        }
        else
        {
            Log.Warn($"Switcher rejected session {id} (Alt already released).");
        }
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
                Interlocked.Increment(ref HostHealth.NativeFallbacks);
                Log.Warn($"Overlay did not acknowledge session {_pendingSession} within {b.AckTimeoutMs} ms — used native Alt+Tab.");
                // Let the switcher prove it is alive again right away instead of at the next periodic check.
                PostMessageW(b.TargetWindow, HookMessages.Ping, 0, 0);
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
                Log.Info($"Alt is up but no release was seen → commit session {b.SessionId}.");
                Commit(b.SessionId);
            }
        }
        else if (id == _reinstallTimer)
        {
            if (!b.IsSessionActive) Reinstall("periodic");
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
        if (SendInput(2, inputs, sizeof(INPUT)) != 2)
            Log.Debug($"SendInput(dummy key) failed: {ErrorText.Win32(Marshal.GetLastPInvokeError())}");
    }

    /// <summary>Re-sends the swallowed Tab so Windows' own switcher handles this Alt+Tab.</summary>
    private static void ReplayToNative(bool reverse)
    {
        INPUT* inputs = stackalloc INPUT[8];
        uint n = 0;
        bool altHeld = IsKeyDown(VK_MENU);
        bool shiftHeld = IsKeyDown(VK_SHIFT);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, false, HookBridge.InjectionMarker);
        if (reverse && !shiftHeld) inputs[n++] = INPUT.Key(VK_LSHIFT, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_TAB, false, HookBridge.InjectionMarker);
        inputs[n++] = INPUT.Key(VK_TAB, true, HookBridge.InjectionMarker);
        if (reverse && !shiftHeld) inputs[n++] = INPUT.Key(VK_LSHIFT, true, HookBridge.InjectionMarker);
        if (!altHeld) inputs[n++] = INPUT.Key(VK_LMENU, true, HookBridge.InjectionMarker);
        uint sent = SendInput(n, inputs, sizeof(INPUT));
        if (sent != n) ErrorText.LogWin32Failure("SendInput(native Alt+Tab replay)", sent, Marshal.GetLastPInvokeError());
        else Log.Info($"Replayed {(reverse ? "Alt+Shift+Tab" : "Alt+Tab")} to Windows (native switcher).");
    }

    // ───────────────────────────────── key state ─────────────────────────────────

    private static bool IsAlt(uint vk) => vk is VK_LMENU or VK_RMENU or VK_MENU;

    private static bool IsModifier(uint vk) =>
        vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT or VK_LCONTROL or VK_RCONTROL or VK_CONTROL or VK_CAPITAL;

    /// <summary>
    /// Inside a low-level hook the async key state is not yet updated for the key being
    /// processed, so the current event is taken into account explicitly. For other keys the
    /// event's own LLKHF_ALTDOWN flag is authoritative; GetAsyncKeyState covers the rest.
    /// </summary>
    private static bool AltDown(KBDLLHOOKSTRUCT* k, uint vk, bool up) => vk switch
    {
        VK_LMENU => !up || IsKeyDown(VK_RMENU),
        VK_RMENU => !up || IsKeyDown(VK_LMENU),
        VK_MENU => !up,
        _ => (k->flags & LLKHF_ALTDOWN) != 0 || IsKeyDown(VK_MENU),
    };

    private static bool ShiftDown() => IsKeyDown(VK_SHIFT);

    /// <summary>Ctrl held by the user — the synthetic Ctrl of AltGr does not count.</summary>
    private bool CtrlDown()
    {
        if (IsKeyDown(VK_RCONTROL)) return true;
        if (!IsKeyDown(VK_LCONTROL)) return false;
        return !_altGrControlDown || _realLeftControlDown;
    }

    private static bool WinDown() => IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
}
