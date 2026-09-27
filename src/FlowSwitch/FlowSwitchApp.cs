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
    private readonly string _localData;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private Watchdog? _watchdog;
    private bool _safeMode;
    private string? _safeModeReason;
    private DateTimeOffset? _pausedUntil;
    private Timer? _pauseTimer;
    private uint _commandMessage;
    private uint _taskbarCreated;

    public FlowSwitchApp(string[] args, SettingsService settings, CrashGuard crash, string localData)
    {
        _args = args;
        _settings = settings;
        _crash = crash;
        _localData = localData;
        _hook = new KeyboardHook(_bridge);
        _switcher = new SwitcherThread(_bridge, Path.Combine(localData, "ShaderCache"));
    }

    public int Run()
    {
        var settings = _settings.Current;
        _crash.AutoRestart = settings.Advanced.AutoRestartAfterCrash;
        _safeMode = _crash.ShouldStartInSafeMode(out _safeModeReason);
        _pausedUntil = _settings.Store.LoadState().PausedUntil is { } until && until > DateTimeOffset.Now ? until : null;

        _commandMessage = RegisterWindowMessageW(IpcProtocol.CommandMessage);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _window = new MainWindow(this);
        // When started elevated (Settings → Advanced), UIPI would drop these from normal-integrity senders.
        ChangeWindowMessageFilterEx(_window.Handle, _commandMessage, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(_window.Handle, _taskbarCreated, MSGFLT_ALLOW, 0);
        _tray = new TrayIcon(_window.Handle);

        _switcher.Start(settings);
        ApplyEnabledState();
        _hook.Start();
        _watchdog = new Watchdog(_bridge, () => _switcher.OverlayVisible, () => _switcher.WindowHandle, _crash);
        _watchdog.Start();

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
        Log.Info($"FlowSwitch running (safe mode: {_safeMode}, args: {string.Join(' ', _args)}).");

        while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
        return 0;
    }

    // ───────────────────────────────── state ─────────────────────────────────

    private bool IsPaused => _pausedUntil is { } until && until > DateTimeOffset.Now;

    private void ApplyEnabledState()
    {
        var settings = _settings.Current;
        bool active = settings.General.Enabled && !_safeMode && !IsPaused;
        _bridge.Enabled = active;
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
            _crash.ClearSafeMode();
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

    private void OnCommand(int command)
    {
        switch (command)
        {
            case IpcProtocol.OpenSettings:
                SettingsLauncher.Launch();
                break;
            case IpcProtocol.ShowPreview:
                _switcher.Post(c => c.ShowPreview());
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
    }

    public void Dispose()
    {
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
                _app.OnCommand((int)wParam);
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
