using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Core.Settings;
using FlowSwitch.Diagnostics;
using FlowSwitch.Input;
using FlowSwitch.Overlay;
using FlowSwitch.Services;
using FlowSwitch.Shell;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch;

/// <summary>
/// Composition root of the resident process. Owns the main (tray) thread; the keyboard hook and
/// the switcher each run on their own threads.
/// </summary>
internal sealed class FlowSwitchApp : IDisposable
{
    private const uint WmSettingsChanged = WM_APP + 1;
    private const uint WmResumeTimer = WM_APP + 2;

    private readonly string[] _args;
    private readonly SettingsService _settings;
    private readonly CrashGuard _crash;
    private readonly HookBridge _bridge = new();
    private readonly KeyboardHook _hook;
    private readonly SwitcherThread _switcher;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private Watchdog? _watchdog;
    private bool _safeMode;
    private string? _safeModeReason;
    private DateTimeOffset? _pausedUntil;
    private Timer? _pauseTimer;
    private uint _commandMessage;
    private uint _taskbarCreated;

    private const int DiagnosticHotkeyId = 0xF12;

    public FlowSwitchApp(string[] args, SettingsService settings, CrashGuard crash)
    {
        _args = args;
        _settings = settings;
        _crash = crash;
        _hook = new KeyboardHook(_bridge);
        _switcher = new SwitcherThread(_bridge, FlowSwitchPaths.ShaderCache);
    }

    public int Run()
    {
        var settings = _settings.Current;
        HostHealth.Elevated = SystemDescription.IsElevated;
        _crash.AutoRestart = settings.Advanced.AutoRestartAfterCrash;
        _safeMode = _crash.ShouldStartInSafeMode(out _safeModeReason);
        _pausedUntil = _settings.Store.LoadState().PausedUntil is { } until && until > DateTimeOffset.Now ? until : null;
        if (_safeMode) Log.Warn($"SAFE MODE is ON — the keyboard hook will not take Alt+Tab. Reason: {_safeModeReason ?? "(none recorded)"}");
        else Log.Info("Safe mode: off.");
        if (_pausedUntil is { } p) Log.Info($"Paused until {p.LocalDateTime:g}.");

        string projection = Capture.CaptureProbe.CheckProjection(out bool projectionBroken);
        HostHealth.WinRtProjection = projection;
        HostHealth.WinRtProjectionBroken = projectionBroken;
        if (projectionBroken) Log.Error($"WinRT projection problem — live previews will be unavailable: {projection}. Reinstall FlowSwitch from a complete package.");
        else Log.Info($"WinRT projection: {projection}.");

        _commandMessage = RegisterWindowMessageW(IpcProtocol.CommandMessage);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _window = new MainWindow(this);
        Log.Info($"Host window created: 0x{_window.Handle:X} (class {IpcProtocol.HostWindowClass}).");
        // When started elevated (Settings → Advanced), UIPI would drop these from normal-integrity senders.
        ChangeWindowMessageFilterEx(_window.Handle, _commandMessage, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(_window.Handle, _taskbarCreated, MSGFLT_ALLOW, 0);
        _tray = new TrayIcon(_window.Handle);

        _bridge.HostWindow = _window.Handle;
        _switcher.Start(settings);
        ApplyEnabledState();
        _hook.Start();
        _watchdog = new Watchdog(_bridge, () => _switcher.OverlayVisible, () => _switcher.WindowHandle, _crash);
        _watchdog.Start();
        RegisterDiagnosticHotkey();

        _settings.Changed += _ => PostMessageW(_window.Handle, WmSettingsChanged, 0, 0);
        StartupRegistration.Apply(settings.General.LaunchAtStartup, settings.Advanced.RunElevated);
        SchedulePauseEnd();

        if (_safeMode)
        {
            _tray.ShowBalloon("FlowSwitch is paused", (_safeModeReason ?? "FlowSwitch stopped unexpectedly.") +
                " Windows Alt+Tab is active. Enable FlowSwitch from this icon to try again.", warning: true);
        }
        else if (!settings.General.OnboardingCompleted && !_args.Contains("--recover"))
        {
            SettingsLauncher.Launch("--onboarding");
        }
        Log.Info($"FlowSwitch running (safe mode: {_safeMode}, enabled: {settings.General.Enabled}, paused: {IsPaused}). " +
                 "Alt+Tab is taken over once the renderer reports ready.");

        int result;
        while ((result = GetMessageW(out MSG msg, 0, 0, 0)) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
        if (result < 0) ErrorText.LogWin32Failure("GetMessageW (main thread)", result, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        return 0;
    }

    /// <summary>
    /// Ctrl+Alt+F12 opens the overlay directly. It uses RegisterHotKey, not the keyboard hook, so it
    /// tells "the hook does not see Alt+Tab" apart from "the overlay cannot be shown".
    /// </summary>
    private void RegisterDiagnosticHotkey()
    {
        // No MOD_NOREPEAT: some environments never match it. Auto-repeat is debounced in WndProc.
        if (RegisterHotKey(_window!.Handle, DiagnosticHotkeyId, MOD_CONTROL | MOD_ALT, VK_F12))
        {
            HostHealth.DiagnosticHotkey = true;
            Log.Info("Diagnostic hotkey Ctrl+Alt+F12 registered (opens the overlay without Alt+Tab).");
        }
        else
        {
            ErrorText.LogWin32Failure("RegisterHotKey(Ctrl+Alt+F12)", false, System.Runtime.InteropServices.Marshal.GetLastPInvokeError(),
                "another app owns this shortcut — use Settings → Diagnostics → Test overlay instead");
        }
    }

    // ───────────────────────────────── state ─────────────────────────────────

    private bool _enabledStateLogged;

    private bool IsPaused => _pausedUntil is { } until && until > DateTimeOffset.Now;

    private void ApplyEnabledState()
    {
        var settings = _settings.Current;
        bool active = settings.General.Enabled && !_safeMode && !IsPaused;
        if (_bridge.Enabled != active || !_enabledStateLogged)
        {
            _enabledStateLogged = true;
            Log.Info($"Alt+Tab takeover {(active ? "enabled" : "disabled")} (setting enabled: {settings.General.Enabled}, safe mode: {_safeMode}, paused: {IsPaused}).");
        }
        _bridge.Enabled = active;
        HostHealth.Enabled = settings.General.Enabled;
        HostHealth.SafeMode = _safeMode;
        HostHealth.SafeModeReason = _safeModeReason;
        HostHealth.Paused = IsPaused;
        string tip = !settings.General.Enabled || _safeMode ? "FlowSwitch — off (Windows Alt+Tab)"
            : IsPaused ? $"FlowSwitch — paused until {_pausedUntil!.Value.LocalDateTime:t}"
            : "FlowSwitch — Alt+Tab";
        if (_tray is not null)
        {
            _tray.PausedUntil = IsPaused ? _pausedUntil : null;
            _tray.SetState(settings.General.Enabled && !_safeMode, IsPaused, tip);
        }
    }

    private void OnSettingsChanged()
    {
        var settings = _settings.Current;
        _crash.AutoRestart = settings.Advanced.AutoRestartAfterCrash;
        _switcher.Post(c => c.ApplySettings(settings));
        StartupRegistration.Apply(settings.General.LaunchAtStartup, settings.Advanced.RunElevated);
        ApplyEnabledState();
    }

    private void SetEnabled(bool enabled)
    {
        if (enabled && _safeMode)
        {
            _safeMode = false;
            _safeModeReason = null;
            _crash.ClearSafeMode();
            Log.Info("Safe mode cleared by the user.");
        }
        _settings.Update(s => s.General.Enabled = enabled);
        ApplyEnabledState();
    }

    private void Pause(TimeSpan? duration)
    {
        _pausedUntil = duration is { } d ? DateTimeOffset.Now + d : null;
        var state = _settings.Store.LoadState();
        state.PausedUntil = _pausedUntil;
        try { _settings.Store.SaveState(state); } catch { /* not critical */ }
        SchedulePauseEnd();
        ApplyEnabledState();
    }

    private void SchedulePauseEnd()
    {
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        if (_pausedUntil is not { } until || _window is null) return;
        var due = until - DateTimeOffset.Now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        nint hwnd = _window.Handle;
        _pauseTimer = new Timer(_ => PostMessageW(hwnd, WmResumeTimer, 0, 0), null, due, Timeout.InfiniteTimeSpan);
    }

    private void Execute(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenSettings:
                SettingsLauncher.Launch();
                break;
            case TrayCommand.Preview:
                _switcher.Post(c => c.ShowPreview());
                break;
            case TrayCommand.ToggleEnabled:
                SetEnabled(!(_settings.Current.General.Enabled && !_safeMode));
                break;
            case TrayCommand.Pause:
                Pause(TimeSpan.FromHours(1));
                break;
            case TrayCommand.Resume:
                Pause(null);
                break;
            case TrayCommand.Restart:
                CrashGuard.Relaunch("--restart");
                PostQuitMessage(0);
                break;
            case TrayCommand.Exit:
                PostQuitMessage(0);
                break;
        }
    }

    /// <summary>Handles a Settings / second-instance command. Returns the message result.</summary>
    private nint OnCommand(int command, nint lParam)
    {
        if (command != IpcProtocol.QueryStatus) Log.Info($"IPC command {command} received.");
        switch (command)
        {
            case IpcProtocol.QueryStatus:
                return (nint)(long)HostHealth.ToStatus();
            case IpcProtocol.OpenSettings:
                SettingsLauncher.Launch();
                break;
            case IpcProtocol.ShowPreview:
                _switcher.Post(c => c.ShowPreview());
                break;
            case IpcProtocol.TestOverlay:
                _switcher.Post(c => c.ShowTestOverlay("Settings → Test overlay"));
                break;
            case IpcProtocol.RunDiagnostics:
                SelfTest.Start((int)lParam, _bridge, _switcher, $"FlowSwitch {typeof(Program).Assembly.GetName().Version}");
                break;
            case IpcProtocol.ExitSafeMode:
                SetEnabled(true);
                break;
            case IpcProtocol.ReloadSettings:
                _settings.Reload();
                break;
            case IpcProtocol.TogglePause:
                Pause(IsPaused ? null : TimeSpan.FromHours(1));
                break;
            case IpcProtocol.Restart:
                Execute(TrayCommand.Restart);
                break;
            case IpcProtocol.Exit:
                PostQuitMessage(0);
                break;
            case IpcProtocol.OnboardingFinished:
                _settings.Reload();
                _tray?.ShowBalloon("FlowSwitch is ready", "Press Alt+Tab.");
                break;
        }
        return 1;
    }

    public void Dispose()
    {
        if (_window is not null && HostHealth.DiagnosticHotkey) UnregisterHotKey(_window.Handle, DiagnosticHotkeyId);
        _pauseTimer?.Dispose();
        _watchdog?.Dispose();
        _hook.Dispose();
        _switcher.Dispose();
        _tray?.Dispose();
        _window?.Dispose();
    }

    /// <summary>Hidden top-level window: tray callbacks, IPC commands, power / session notifications.</summary>
    private sealed class MainWindow : NativeWindow
    {
        private const uint PBT_APMRESUMEAUTOMATIC = 0x12;
        private const uint WTS_SESSION_UNLOCK = 0x8;
        private readonly FlowSwitchApp _app;
        private const nuint HookHotkeyTimer = 0xF12;
        private long _lastHotkey = -10_000;

        public MainWindow(FlowSwitchApp app)
        {
            _app = app;
            CreateHandle(IpcProtocol.HostWindowClass, WS_EX_TOOLWINDOW, WS_POPUP, 0, 0, 0, 0);
        }

        protected override nint WndProc(uint msg, nuint wParam, nint lParam, out bool handled)
        {
            handled = true;
            if (msg == TrayIcon.CallbackMessage)
            {
                _app.Execute(_app._tray!.HandleCallback(wParam, lParam));
                return 0;
            }
            if (msg == _app._commandMessage && msg != 0)
            {
                return _app.OnCommand((int)wParam, lParam);
            }
            if (msg == WM_HOTKEY && (int)wParam == DiagnosticHotkeyId)
            {
                KillTimer(Handle, HookHotkeyTimer);
                if (Environment.TickCount64 - _lastHotkey < 1500) return 0; // held key auto-repeat
                _lastHotkey = Environment.TickCount64;
                Log.Info("Ctrl+Alt+F12 pressed (RegisterHotKey, not the keyboard hook) — opening the test overlay.");
                _app._switcher.Post(c => c.ShowTestOverlay("Ctrl+Alt+F12"));
                return 0;
            }
            if (msg == HookBridge.DiagnosticKeyMessage)
            {
                // The hook sees keys before RegisterHotKey: give the real hotkey 250 ms to arrive.
                SetTimer(Handle, HookHotkeyTimer, 250, 0);
                return 0;
            }
            if (msg == WM_TIMER && wParam == HookHotkeyTimer)
            {
                KillTimer(Handle, HookHotkeyTimer);
                if (Environment.TickCount64 - _lastHotkey < 1500) return 0;
                _lastHotkey = Environment.TickCount64;
                Log.Info("Ctrl+Alt+F12 seen by the keyboard hook (RegisterHotKey did not deliver it) — opening the test overlay.");
                _app._switcher.Post(c => c.ShowTestOverlay("Ctrl+Alt+F12 via keyboard hook"));
                return 0;
            }
            if (msg == _app._taskbarCreated && msg != 0)
            {
                _app._tray?.Recreate();
                _app.ApplyEnabledState();
                return 0;
            }
            switch (msg)
            {
                case WmSettingsChanged:
                    _app.OnSettingsChanged();
                    return 0;
                case WmResumeTimer:
                    if (!_app.IsPaused) _app.Pause(null);
                    return 0;
                case WM_POWERBROADCAST when wParam == PBT_APMRESUMEAUTOMATIC:
                    _app._hook.RequestReinstall();
                    handled = false;
                    return 0;
                case WM_WTSSESSION_CHANGE when wParam == WTS_SESSION_UNLOCK:
                    _app._hook.RequestReinstall();
                    return 0;
                case WM_SETTINGCHANGE:
                case WM_DISPLAYCHANGE:
                    // Reduced-motion, power and monitor state are re-read at the next Alt+Tab.
                    handled = false;
                    return 0;
                case WM_CLOSE:
                    PostQuitMessage(0);
                    return 0;
            }
            handled = false;
            return 0;
        }
    }
}
