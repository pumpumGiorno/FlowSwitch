using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using FlowSwitch.Capture;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Model;
using FlowSwitch.Core.Scene;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;
using FlowSwitch.Diagnostics;
using FlowSwitch.Input;
using FlowSwitch.Interop;
using FlowSwitch.Rendering;
using FlowSwitch.Services;
using FlowSwitch.WindowManagement;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Overlay;

/// <summary>
/// Runs one switcher invocation end to end on the switcher thread: hook messages in, session
/// logic, animation, live captures, rendering and window activation out.
/// </summary>
/// <remarks>
/// Lifecycle: Idle → Pending (Alt+Tab pressed; windows enumerated, captures warming, overlay not
/// yet visible) → Visible → Closing (exit animation) → Idle. A release during Pending is a quick
/// switch: the target is activated instantly and only a subtle highlight is drawn.
/// </remarks>
internal sealed class SwitcherController : IDisposable
{
    private enum State { Idle, Pending, Visible, Closing, Flash }

    private readonly HookBridge _bridge;
    private readonly AppIdentityResolver _identities = new();
    private readonly VirtualDesktopService _desktops = new();
    private readonly WindowTracker _tracker;
    private readonly IconService _icons = new();
    private readonly SwitcherAnimator _animator = new();
    private readonly SceneComposer _composer = new();
    private readonly LayoutContext _layoutContext = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, ColorF> _accents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, double> _previewDue = new();
    private readonly List<OverlaySurface> _secondary = new();
    private readonly string _shaderCache;

    private FlowSwitchSettings _settings = new();
    private MotionProfile _motion = MotionProfile.Smooth;
    private QualityProfile _quality = QualityProfile.High;
    private QualityProfile _sessionQuality = QualityProfile.High;
    private ScenePalette _palette = new();
    private ILayoutEngine _layout = LayoutFactory.Create(SwitcherMode.SolarSystem);

    private GraphicsDevice? _gfx;
    private Renderer? _renderer;
    private CaptureService? _capture;
    private OverlaySurface? _primary;
    private SceneInput? _input;

    private State _state = State.Idle;
    private SwitcherSession? _session;
    private int _sessionId;
    private double _sessionStart;
    private double _revealAt;
    private double _lastFrame;
    private long _frameIndex;
    private bool _windowsDirty;
    private double _lastWindowRefresh;
    private MonitorDescriptor? _monitor;
    private Vector2? _pointer;
    private Vector2 _pointerAtReveal;
    private HitResult _pressed;
    private int _wheelAccumulator;
    private double _prewarmUntil;
    private double _lastBackdropUpdate;
    private bool _backdropFrozen;
    private IReadOnlyList<VirtualDesktop> _desktopList = Array.Empty<VirtualDesktop>();

    // Quick-switch highlight.
    private Vector4 _flashRect;
    private ColorF _flashAccent;
    private double _flashStart;

    // Adaptive quality.
    private double _slowFrameTime;
    private int _frameSamples;

    public SwitcherController(HookBridge bridge, string shaderCache)
    {
        _bridge = bridge;
        _shaderCache = shaderCache;
        _tracker = new WindowTracker(_identities, _desktops);
        _tracker.ForegroundChanged += OnForegroundChanged;
        _tracker.WindowsChanged += () => _windowsDirty = true;
        _tracker.WindowDiscovered += OnWindowDiscovered;
        _tracker.Start();
    }

    /// <summary>Read by the watchdog thread.</summary>
    public volatile bool OverlayVisible;

    /// <summary>Raised when the icon thread has results (used to wake the loop while idle).</summary>
    public event Action? WakeRequested
    {
        add => _icons.ResultReady += value;
        remove => _icons.ResultReady -= value;
    }

    public bool GraphicsAvailable => _gfx is not null && _renderer is not null && _primary is not null;

    public bool WantsFrames => _state != State.Idle || Now < _prewarmUntil;

    /// <summary>Handle the render loop waits on (0 → use a short timeout).</summary>
    public nint FrameWaitable => _state is State.Visible or State.Closing or State.Flash && _primary is { } p ? p.FrameLatencyWaitable : 0;

    /// <summary>When the next automatic graphics retry is due (seconds on the controller clock), or +∞.</summary>
    public double GraphicsRetryAt { get; private set; } = double.PositiveInfinity;

    private double Now => _clock.Elapsed.TotalSeconds;

    // ═════════════════════════════════════ setup ═════════════════════════════════════

    private static readonly double[] RetryDelays = { 5, 15, 60, 300 };
    private int _graphicsFailures;

    /// <summary>
    /// Creates the Direct3D device, shaders, renderer and overlay window. Only when all of them
    /// work does the keyboard hook start taking over Alt+Tab (<see cref="HookBridge.RendererReady"/>).
    /// On failure the error is logged with its stage and HRESULT, Alt+Tab stays with Windows, and
    /// another attempt is scheduled (5 s, 15 s, 1 min, then every 5 min).
    /// </summary>
    public bool InitializeGraphics(string reason = "startup")
    {
        if (GraphicsAvailable) return true;
        if (!_settingsApplied) return false;
        _bridge.RendererReady = false;
        HostHealth.Renderer = ComponentState.Starting;
        HostHealth.RendererStage = null;
        var sw = Stopwatch.StartNew();
        Log.Info($"Renderer initialisation started ({reason}, attempt {_graphicsFailures + 1}).");
        string stage = "D3D11 device";
        try
        {
            HostHealth.RendererStage = stage;
            _gfx = GraphicsDevice.Create();
            Log.Info("D3D device created.");

            stage = "shaders / renderer";
            HostHealth.RendererStage = stage;
            HostHealth.Shaders = ComponentState.Starting;
            try
            {
                _renderer = new Renderer(_gfx, _shaderCache);
                HostHealth.Shaders = ComponentState.Ready;
                HostHealth.ShaderError = null;
            }
            catch (Exception ex)
            {
                HostHealth.Shaders = ComponentState.Failed;
                HostHealth.ShaderError = ex is GraphicsInitException gi ? gi.Message : ErrorText.Of(ex);
                throw;
            }

            stage = "overlay window / DirectComposition / swap chain";
            HostHealth.RendererStage = stage;
            HostHealth.Overlay = ComponentState.Starting;
            var monitor = Monitors.Primary();
            try
            {
                _primary = new OverlaySurface(_gfx, monitor?.Bounds ?? new RECT { Right = 1920, Bottom = 1080 });
                HostHealth.Overlay = ComponentState.Ready;
                HostHealth.OverlayError = null;
                HostHealth.OverlayHwnd = _primary.Handle;
            }
            catch (Exception ex)
            {
                HostHealth.Overlay = ComponentState.Failed;
                HostHealth.OverlayError = ex is GraphicsInitException gi ? gi.Message : ErrorText.Of(ex);
                throw;
            }
            _primary.Pointer += OnPointer;
            _input = new SceneInput
            {
                Session = null!,
                Animator = _animator,
                Layout = _layoutContext,
                Palette = _palette,
                Quality = _quality,
                Resources = _renderer,
            };
            Log.Info("Renderer initialized.");

            // Live previews and the blurred backdrop are optional: without them the switcher still works.
            InitializeCapture();

            _graphicsFailures = 0;
            GraphicsRetryAt = double.PositiveInfinity;
            HostHealth.Renderer = ComponentState.Ready;
            HostHealth.RendererError = null;
            HostHealth.RendererStage = null;
            _bridge.RendererReady = true;
            Log.Info($"Graphics ready in {sw.ElapsedMilliseconds} ms — Alt+Tab is now handled by FlowSwitch " +
                     $"(live previews: {(_capture?.Supported == true ? "yes" : "no")}).");
            return true;
        }
        catch (Exception ex)
        {
            if (ex is GraphicsInitException { Stage: { } failedStage }) stage = failedStage;
            string detail = ex is GraphicsInitException gi ? gi.Message : $"{stage}: {ErrorText.Of(ex)}";
            HostHealth.Renderer = ComponentState.Failed;
            HostHealth.RendererError = detail;
            Log.Error($"Graphics initialisation FAILED at stage \"{stage}\" — Alt+Tab stays with Windows. {detail}", ex);
            DisposeGraphics();
            double delay = RetryDelays[Math.Min(_graphicsFailures, RetryDelays.Length - 1)];
            _graphicsFailures++;
            GraphicsRetryAt = Now + delay;
            Log.Info($"Next renderer attempt in {delay:0} s.");
            return false;
        }
    }

    private void InitializeCapture()
    {
        HostHealth.Capture = ComponentState.Starting;
        string projection = CaptureProbe.CheckProjection(out bool broken);
        HostHealth.WinRtProjection = projection;
        HostHealth.WinRtProjectionBroken = broken;
        if (broken) Log.Error($"WinRT projection problem: {projection}");
        else Log.Info($"WinRT projection: {projection}.");

        string? deviceError = null;
        if (broken || !_gfx!.TryCreateWinRTDevice(out deviceError))
        {
            HostHealth.Capture = ComponentState.Failed;
            HostHealth.CaptureError = broken ? projection : deviceError;
            Log.Warn($"Windows Graphics Capture disabled: {HostHealth.CaptureError}. The switcher works without live previews.");
            return;
        }
        _capture = CaptureProbe.TryCreateService(_gfx.WinRTDevice, out string? captureError);
        if (_capture is { Supported: true })
        {
            HostHealth.Capture = ComponentState.Ready;
            HostHealth.CaptureError = null;
            Log.Info("Windows Graphics Capture ready.");
        }
        else
        {
            HostHealth.Capture = ComponentState.Unavailable;
            HostHealth.CaptureError = captureError ?? "not supported";
            Log.Warn($"Windows Graphics Capture unavailable: {HostHealth.CaptureError}.");
        }
    }

    /// <summary>Called by the switcher loop when <see cref="GraphicsRetryAt"/> has passed.</summary>
    public void RetryGraphicsIfDue()
    {
        if (!GraphicsAvailable && Now >= GraphicsRetryAt && _state == State.Idle) InitializeGraphics("scheduled retry");
    }

    /// <summary>Seconds until the next graphics retry (for the loop's wait timeout), or null.</summary>
    public double? SecondsUntilGraphicsRetry => double.IsPositiveInfinity(GraphicsRetryAt) ? null : Math.Max(0, GraphicsRetryAt - Now);

    private bool _settingsApplied;

    public void ApplySettings(FlowSwitchSettings settings)
    {
        _settings = settings;
        _settingsApplied = true;
        RefreshProfiles();
        _layout = LayoutFactory.Create(settings.General.Mode);
        _tracker.IncludeOtherDesktops = settings.General.ShowWindowsFromAllDesktops;
        _tracker.IncludeMinimized = settings.General.ShowMinimizedWindows;
        _tracker.HiddenProcesses = new HashSet<string>(settings.Advanced.HiddenProcesses.Select(NormalizeExe), StringComparer.OrdinalIgnoreCase);

        var hk = settings.Hotkeys;
        _bridge.AltTab = hk.AltTab;
        _bridge.Reverse = hk.AltShiftTabReverse;
        _bridge.Sticky = hk.CtrlAltTabSticky;
        _bridge.SameApp = hk.AltBacktickSameApp;
        _bridge.Prewarm = settings.Performance.PrewarmOnAlt && _quality.Prewarm;
        _bridge.AckTimeoutMs = settings.Advanced.OverlayTimeoutMs;
        Log.MinimumLevel = Program.EffectiveLogLevel(settings);
        _bridge.VerboseInput = settings.Advanced.DiagnosticLogging;
    }

    private void RefreshProfiles()
    {
        _motion = MotionProfile.Resolve(_settings.Animation, _settings.Solar, SystemPreferences.PrefersReducedMotion());
        _quality = QualityProfile.Resolve(_settings.Performance, SystemPreferences.GetPowerState());
        _palette = ScenePalette.Resolve(_settings.Appearance, _settings.Solar, _settings.General.Mode);
        _layoutContext.Solar = _settings.Solar;
        _layoutContext.Motion = _motion;
        _layoutContext.Mode = _settings.General.Mode;
        _layoutContext.CardSize = _settings.Appearance.CardSize;
    }

    private static string NormalizeExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

    // ═════════════════════════════════════ hook messages ═════════════════════════════════════

    public void OnHookMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case HookMessages.Begin:
                Begin((BeginFlags)wParam, (int)lParam);
                break;
            case HookMessages.Key:
                if ((int)((ulong)lParam >> 16) == _sessionId)
                    OnKey((uint)(wParam & 0xFFFF), (uint)((wParam >> 16) & 0xFFFF), (KeyFlags)((ulong)lParam & 0xFFFF));
                break;
            case HookMessages.Commit:
                if ((int)lParam == _sessionId) Commit();
                break;
            case HookMessages.Cancel:
                if ((int)lParam == _sessionId)
                {
                    Log.Info($"Session {_sessionId} cancelled (Esc).");
                    _session?.Cancel();
                }
                break;
            case HookMessages.Abort:
                if ((int)lParam == _sessionId) FinishSession("aborted by the keyboard hook");
                break;
            case HookMessages.Prewarm:
                Prewarm();
                break;
            case HookMessages.PrewarmEnd:
                if (_state == State.Idle) _prewarmUntil = Now + 1.5;
                break;
            case HookMessages.Ping:
                _bridge.ReportAlive();
                break;
        }
    }

    /// <summary>Opens a sticky switcher (tray "Preview", Settings "Try it").</summary>
    public void ShowPreview() => ShowDirect("preview", autoCloseSeconds: 0);

    /// <summary>
    /// Opens the switcher with the real open windows for a few seconds, bypassing the keyboard
    /// hook entirely (Settings "Test overlay", Ctrl+Alt+F12). Separates "the hook does not see
    /// Alt+Tab" from "the overlay cannot be shown".
    /// </summary>
    public void ShowTestOverlay(string origin, double seconds = 5) => ShowDirect(origin, seconds);

    private void ShowDirect(string origin, double autoCloseSeconds)
    {
        Log.Info($"Test overlay requested ({origin}).");
        if (_state != State.Idle) FinishSession("replaced by " + origin, releaseMemory: false);
        if (!GraphicsAvailable && !InitializeGraphics(origin))
        {
            Log.Error($"Test overlay ({origin}) cannot open: renderer unavailable — {HostHealth.RendererError}");
            return;
        }
        int id = _bridge.BeginSession(sticky: true);
        Begin(BeginFlags.Sticky, id, origin);
        if (_session is not null && autoCloseSeconds > 0) _autoCloseAt = Now + autoCloseSeconds;
    }

    private double _autoCloseAt = double.PositiveInfinity;
    private bool _firstFrameLogged;
    private string _origin = "Alt+Tab";

    private void Begin(BeginFlags flags, int id, string origin = "Alt+Tab")
    {
        try
        {
            BeginCore(flags, id, origin);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, this Alt+Tab goes to Windows right now — not after a timeout.
            Log.Error($"Session {id} could not start — handing Alt+Tab to Windows", ex);
            _bridge.RejectSession(id);
            FinishSession("start failed");
        }
    }

    private void BeginCore(BeginFlags flags, int id, string origin)
    {
        Log.Info($"Switcher session {id} requested by {origin} ({flags}).");
        if (_state != State.Idle) FinishSession("superseded by a new session", releaseMemory: false);
        if (!GraphicsAvailable)
        {
            // Never swallow an Alt+Tab we cannot show: the hook replays it to Windows.
            Log.Warn($"Session {id} rejected: renderer not ready ({HostHealth.RendererError ?? "not initialised"}) — native Alt+Tab.");
            _bridge.RejectSession(id);
            return;
        }
        // Answer the hook first: from here on the overlay is committed to this Alt+Tab.
        _bridge.Acknowledge(id);

        RefreshProfiles();
        _sessionQuality = _quality;
        _frameSamples = 0;
        _slowFrameTime = 0;

        var windows = _tracker.Snapshot();
        foreach (var w in windows) ApplyAccent(w);
        Log.Info($"Window enumeration: {windows.Count} windows ({windows.Count(w => w.IsMinimized)} minimized, " +
                 $"{windows.Count(w => !w.IsOnCurrentDesktop)} on other desktops).");

        bool sticky = (flags & BeginFlags.Sticky) != 0;
        var mode = (flags & BeginFlags.SameApp) != 0 ? SessionMode.SameApp : sticky ? SessionMode.Sticky : SessionMode.Standard;
        var session = new SwitcherSession(windows, new SessionOptions
        {
            Mode = mode,
            Reverse = (flags & BeginFlags.Reverse) != 0,
            Grouping = _settings.General.Grouping,
            WrapAround = _settings.General.WrapAround,
            SearchEnabled = _settings.General.SearchOnType,
            ExpandDelay = _settings.General.ExpandDelayMs / 1000f,
        });
        if (session.Entries.Count == 0 && !sticky)
        {
            Log.Info($"Session {id}: nothing to switch to — handing Alt+Tab to Windows.");
            _bridge.RejectSession(id);
            return;
        }

        _session = session;
        _sessionId = id;
        _origin = origin;
        _sessionStart = Now;
        _lastFrame = Now;
        _pointer = null;
        _pressed = default;
        _wheelAccumulator = 0;
        _backdropFrozen = false;
        _autoCloseAt = double.PositiveInfinity;
        _firstFrameLogged = false;

        Log.Info("Overlay creation started.");
        PlaceOverlays();
        Log.Info($"Overlay placed on monitor {_monitor?.DeviceName ?? "?"} {_primary!.Bounds} @ {_monitor?.RefreshRate ?? 0} Hz.");
        _layoutContext.Space = DesignSpace.For(new Vector2(_primary.Width, _primary.Height));
        _animator.Begin(session, _motion);
        _input!.Palette = _palette;
        _input.Quality = _sessionQuality;
        _desktopList = _settings.General.ShowVirtualDesktops ? _desktops.GetDesktops() : Array.Empty<VirtualDesktop>();
        UpdateDesktopInfo(windows);

        _state = State.Pending;
        _revealAt = sticky ? Now : Now + _settings.General.RevealDelayMs / 1000.0;
        if (_capture is not null) StartCaptures();
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Log.Info($"Session {id} ({mode}) open with {session.Entries.Count} entries; selected: {Describe(session.Target)}.");
    }

    private static string Describe(WindowInfo? w) =>
        w is null ? "none" : $"0x{w.Handle:X} {w.App.ExecutableName} \"{Truncate(w.Title, 60)}\"";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private void OnKey(uint vk, uint scan, KeyFlags flags)
    {
        var s = _session;
        if (s is null || s.Outcome != SessionOutcome.Open) return;
        bool shift = (flags & KeyFlags.Shift) != 0;
        bool ctrl = (flags & KeyFlags.Ctrl) != 0;
        var hk = _settings.Hotkeys;

        switch ((int)vk)
        {
            case VK_TAB:
                s.Move(shift ? -1 : 1);
                return; // Tab alone never forces the reveal: fast Alt+Tab+Tab stays instant.
            case VK_OEM_3:
                if (s.Mode == SessionMode.SameApp) s.Move(shift ? -1 : 1);
                else if (!s.CycleWithinGroup(shift ? -1 : 1)) s.Move(shift ? -1 : 1);
                break;
            case VK_LEFT when hk.ArrowNavigation:
                s.Move(-1);
                break;
            case VK_RIGHT when hk.ArrowNavigation:
                s.Move(1);
                break;
            case VK_UP when hk.ArrowNavigation:
            case VK_DOWN when hk.ArrowNavigation:
                int dir = vk == VK_DOWN ? 1 : -1;
                if (!s.CycleWithinGroup(dir)) s.Move(dir);
                break;
            case VK_HOME:
                s.Select(0);
                break;
            case VK_END:
                s.Select(Math.Max(0, s.Entries.Count - 1));
                break;
            case VK_RETURN:
                s.Commit();
                break;
            case VK_DELETE when hk.DeleteCloses:
                s.CloseSelected();
                break;
            case VK_BACK:
                s.Backspace();
                break;
            case 'W' when ctrl && hk.CtrlWCloses:
                s.CloseSelected();
                break;
            case 'P' when ctrl:
                s.TogglePinSelected();
                break;
            default:
                if (ctrl) break;
                string? text = KeyTranslator.ToText(vk, scan, flags);
                if (text is not null && (text != " " || s.IsSearching)) s.AppendQuery(text);
                break;
        }

        // Any key other than Tab means the user is interacting: show the overlay right away.
        if (_state == State.Pending) _revealAt = Now;
    }

    private void Commit()
    {
        var s = _session;
        if (s is null) return;
        if (_state == State.Pending)
        {
            // Quick switch: the overlay never appeared. Switch instantly, then (optionally) trace
            // the activated window with a brief glow — the "minimal animation".
            var target = s.Target;
            s.Commit();
            while (s.TryDequeueEffect(out var effect))
                if (effect is ActivateWindowEffect a) Activate(a.Window);
            bool flash = target is not null && _settings.General.QuickSwitchHighlight && !_motion.Reduced;
            FinishSession("quick switch (Alt released before the overlay appeared)", releaseMemory: !flash);
            if (flash) StartFlash(target!);
            return;
        }
        s.Commit();
    }

    // ═════════════════════════════════════ frame ═════════════════════════════════════

    /// <summary>One iteration of the render loop (called at display refresh rate while active).</summary>
    public void RenderFrame()
    {
        double now = Now;
        float dt = (float)Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        _bridge.ReportAlive();

        try
        {
            if (_state == State.Flash)
            {
                RenderFlash(now);
                return;
            }

            PumpIcons();
            if (_state == State.Idle)
            {
                _capture?.Pump();
                if (now >= _prewarmUntil && _capture is { ActiveCount: > 0 }) _capture.StopAll();
                return;
            }

            var s = _session!;
            if (_state == State.Pending && now >= _revealAt) Reveal(s);
            if (now >= _autoCloseAt && s.Outcome == SessionOutcome.Open)
            {
                Log.Info($"Test overlay: closing after its time ({_origin}).");
                _autoCloseAt = double.PositiveInfinity;
                s.Cancel();
            }

            // Safety net: Alt is up but no commit arrived (e.g. released on the secure desktop).
            if (s.Mode != SessionMode.Sticky && s.Outcome == SessionOutcome.Open && !IsKeyDown(VK_MENU) && now - _sessionStart > 0.05)
                Commit();

            s.Tick(dt);
            ProcessEffects();
            if (_session is null) return;

            if (_windowsDirty && now - _lastWindowRefresh > 0.12)
            {
                _windowsDirty = false;
                _lastWindowRefresh = now;
                var windows = _tracker.Snapshot();
                foreach (var w in windows) ApplyAccent(w);
                s.UpdateWindows(windows);
                UpdateDesktopInfo(windows);
                if (_capture is not null) StartCaptures();
            }

            if (_capture is not null) PumpCaptures(now);

            // Nothing animates while the overlay is still hidden: the entrance starts when it appears.
            if (_state == State.Pending) return;

            Vector2? normalized = null;
            if (_pointer is { } p && _primary is not null)
                normalized = new Vector2(p.X / _primary.Width * 2f - 1f, p.Y / _primary.Height * 2f - 1f);
            _animator.HoveredKey = _pointer is { } hp && _state == State.Visible ? HoverKey(_composer.HitTest(hp)) : null;
            _animator.Update(dt, s, _layout, _layoutContext, _renderer!, _palette, normalized);

            Draw(now, dt);
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                HostHealth.FirstFrameRendered = true;
                Log.Info($"First frame rendered and presented for session {_sessionId} ({(now - _sessionStart) * 1000:0} ms after the request).");
            }
            // Show the windows only after their first frame is presented, so no stale content flashes.
            if (_primary is { IsVisible: false })
            {
                ShowOverlays();
                Log.Info($"Overlay shown for session {_sessionId}.");
            }
            _consecutiveFrameFailures = 0;
            if (_animator.IsFinished) FinishSession("exit animation finished");
        }
        catch (Exception ex)
        {
            OnFrameFailure(ex);
        }
    }

    private int _consecutiveFrameFailures;

    /// <summary>
    /// A frame threw: close the overlay, give this Alt+Tab to Windows if Alt is still held, and
    /// after repeated failures (or a lost device) take the renderer out of service until it has
    /// been recreated, so Alt+Tab keeps working natively in the meantime.
    /// </summary>
    private void OnFrameFailure(Exception ex)
    {
        _consecutiveFrameFailures++;
        HostHealth.LastFrameError = ErrorText.Of(ex);
        Log.Error($"Frame failed (#{_consecutiveFrameFailures}) — closing the switcher", ex);
        int id = _sessionId;
        bool wasOpen = _session is not null && _session.Mode != SessionMode.Sticky;
        if (wasOpen) _bridge.RejectSession(id);
        FinishSession("frame failure");

        bool lost = false;
        try { lost = _gfx?.IsLost == true; } catch { lost = true; }
        if (lost || _consecutiveFrameFailures >= 2)
        {
            string why = lost ? $"GPU device lost ({SafeRemovedReason()})" : "frames keep failing";
            Log.Warn($"Renderer taken out of service: {why}. Windows Alt+Tab is used until it is recreated.");
            _bridge.RendererReady = false;
            HostHealth.Renderer = ComponentState.Failed;
            HostHealth.RendererError = $"{why}: {HostHealth.LastFrameError}";
            DisposeGraphics();
            GraphicsRetryAt = Now + (lost ? 1 : RetryDelays[Math.Min(_graphicsFailures, RetryDelays.Length - 1)]);
            _graphicsFailures++;
            _consecutiveFrameFailures = 0;
        }
    }

    private string SafeRemovedReason()
    {
        try
        {
            return _gfx?.DeviceRemovedReason ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private void Draw(double now, float dt)
    {
        var input = _input!;
        input.Session = _session!;
        input.Quality = _sessionQuality;
        input.Palette = _palette;
        input.Pointer = _pointer;
        input.FrameIndex = _frameIndex++;
        input.BackdropLevels = _renderer!.Backdrop?.Levels ?? _sessionQuality.BackdropBlurLevels;
        input.Stats = _settings.Performance.ShowFrameStats
            ? $"{1f / MathF.Max(dt, 1e-3f):0} fps · {dt * 1000f:0.0} ms · {_capture?.ActiveCount ?? 0} captures · {_sessionQuality.Preset}"
            : null;

        var list = _composer.Compose(input);
        _renderer.Render(_primary!.RenderTarget, _primary.Width, _primary.Height, list, useBackdrop: true);
        if (!_primary.Present(vsync: true) && _gfx!.IsLost)
            throw new GraphicsInitException("IDXGISwapChain.Present", $"device removed: {_gfx.DeviceRemovedReason}");

        foreach (var overlay in _secondary)
        {
            var scrim = _composer.ComposeScrim(input, new Vector2(overlay.Width, overlay.Height));
            _renderer.Render(overlay.RenderTarget, overlay.Width, overlay.Height, scrim, useBackdrop: false);
            overlay.Present(vsync: false);
        }

        TrackFrameTime(dt);
    }

    private void Reveal(SwitcherSession session)
    {
        if (_primary is null) return;
        // Restart the animation state from the current selection (Tab may have been pressed
        // several times while the overlay was still pending).
        _animator.Begin(session, _motion);
        _state = State.Visible;
        OverlayVisible = true;
        _pointerAtReveal = GetCursorOnOverlay() ?? Vector2.Zero;
    }

    private void ShowOverlays()
    {
        _primary?.Show();
        foreach (var o in _secondary) o.Show();
    }

    private void ProcessEffects()
    {
        var s = _session;
        if (s is null) return;
        while (s.TryDequeueEffect(out var effect))
        {
            switch (effect)
            {
                case ActivateWindowEffect a:
                    Activate(a.Window);
                    break;
                case DismissEffect d:
                    _bridge.EndSession(_sessionId);
                    if (_state == State.Pending)
                    {
                        FinishSession(d.Committed ? "committed" : "cancelled");
                        return;
                    }
                    Log.Info($"Session {_sessionId} {(d.Committed ? "committed" : "cancelled")} — exit animation.");
                    _animator.BeginExit(d.Committed, s.Selected?.Key, d.Committed ? ExitRect(s.Target) : null);
                    _state = State.Closing;
                    break;
                case CloseWindowEffect c:
                    WindowActivator.Close(c.Window.Handle);
                    _windowsDirty = true;
                    break;
                case TogglePinEffect p:
                    _tracker.TogglePin(p.Window.Handle);
                    _windowsDirty = true;
                    break;
                case SwitchDesktopEffect sd:
                    var current = _desktopList.FirstOrDefault(x => x.IsCurrent);
                    if (current is not null && sd.DesktopIndex != current.Index)
                    {
                        s.Cancel();
                        _desktops.SwitchTo(current.Index, sd.DesktopIndex);
                    }
                    break;
            }
        }
    }

    private static void Activate(WindowInfo window)
    {
        Log.Info($"Activating HWND {Describe(window)}.");
        bool ok = WindowActivator.Activate(window.Handle);
        Log.Info(ok ? $"Activated 0x{window.Handle:X}." : $"Activation of 0x{window.Handle:X} did not bring it to the foreground.");
    }

    /// <summary>The activated window's frame in overlay pixels, if it is on the overlay's monitor.</summary>
    private Vector4? ExitRect(WindowInfo? target)
    {
        if (target is null || _primary is null || target.Bounds.IsEmpty) return null;
        var b = target.Bounds;
        var m = _primary.Bounds;
        if (b.CenterX < m.Left || b.CenterX > m.Right || b.CenterY < m.Top || b.CenterY > m.Bottom) return null;
        return new Vector4(b.X - m.Left, b.Y - m.Top, b.Width, b.Height);
    }

    private void FinishSession(string reason, bool releaseMemory = true)
    {
        if (_session is not null || _state != State.Idle)
            Log.Info($"Session {_sessionId} closed ({reason}, {(Now - _sessionStart) * 1000:0} ms).");
        _autoCloseAt = double.PositiveInfinity;
        if (_sessionId != 0) _bridge.EndSession(_sessionId);
        _primary?.Hide();
        foreach (var o in _secondary) o.Hide();
        OverlayVisible = false;
        _session = null;
        _state = State.Idle;
        _animator.Finish();
        _capture?.StopAll();
        _renderer?.MarkPreviewsStale();
        _renderer?.InvalidateBackdrop();
        _prewarmUntil = 0;
        _previewDue.Clear();
        GCSettings.LatencyMode = GCLatencyMode.Interactive;
        if (_renderer is not null && _renderer.Text.Clock > 2000) _renderer.Text.Trim(_renderer.Text.Clock - 1500);
        if (releaseMemory && _gfx is not null)
        {
            // Nothing renders until the next Alt+Tab: hand the driver's scratch memory back.
            // Every frame binds its full pipeline state, so clearing it here is free.
            _gfx.Context.ClearState();
            _gfx.Trim();
        }
    }

    // ═════════════════════════════════════ captures ═════════════════════════════════════

    private void Prewarm()
    {
        if (_state != State.Idle || !GraphicsAvailable || _capture is null || !_quality.Prewarm) return;
        UpdatePassthrough(GetForegroundWindow());
        var monitor = PickMonitor();
        if (monitor is not null) _capture.EnsureMonitor(monitor.Handle);
        if (_quality.LivePreviews)
        {
            foreach (var w in _tracker.Snapshot().Take(4))
                if (!w.IsMinimized && w.IsOnCurrentDesktop) _capture.EnsureWindow(w.Handle);
        }
        _prewarmUntil = Now + 4.0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void StartCaptures()
    {
        if (_capture is null || _session is null) return;
        if (_monitor is not null) _capture.EnsureMonitor(_monitor.Handle);
        if (!_sessionQuality.LivePreviews) return;

        var keep = new HashSet<long>();
        // Selected entry first, then outward: the worker creates sessions in this order.
        var entries = _session.Entries;
        int sel = Math.Max(0, _session.SelectedIndex);
        for (int k = 0; k < entries.Count; k++)
        {
            int i = (sel + (k % 2 == 0 ? k / 2 : -(k / 2 + 1)) + entries.Count * 4) % entries.Count;
            foreach (var w in entries[i].Windows)
            {
                if (w.IsMinimized || !w.IsOnCurrentDesktop) continue;
                keep.Add(w.Handle);
                _capture.EnsureWindow(w.Handle);
            }
        }
        _capture.StopWindowsExcept(keep);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void PumpCaptures(double now)
    {
        var capture = _capture;
        var renderer = _renderer;
        if (capture is null || renderer is null) return;
        capture.Pump();

        // Blurred desktop.
        if (capture.Monitor is { } monitor && !_backdropFrozen && _primary is not null)
        {
            float fps = _sessionQuality.BackdropFps;
            bool due = !renderer.HasBackdrop || (fps > 0 && now - _lastBackdropUpdate >= 1.0 / fps);
            if (due && TryConsume(monitor, (tex, w, h) => renderer.UpdateBackdrop(tex, w, h, _primary.Width, _primary.Height, _sessionQuality.BackdropBlurLevels)))
            {
                _lastBackdropUpdate = now;
                if (fps <= 0)
                {
                    // Battery saver: one frame is enough.
                    _backdropFrozen = true;
                    capture.StopMonitor();
                }
            }
        }

        if (!_sessionQuality.LivePreviews) return;
        foreach (var card in _animator.Cards)
        {
            float focus = card.Pose.Focus;
            bool near = focus > 0.02f || card.Pose.Depth < 0.5f;
            float fps = focus > 0.5f ? _sessionQuality.SelectedPreviewFps : near ? _sessionQuality.NearPreviewFps : _sessionQuality.FarPreviewFps;
            foreach (var w in card.Entry.Windows)
            {
                if (capture.Window(w.Handle) is not { } source) continue;
                bool hasPreview = renderer.GetPreview(w.Handle) is { Available: true, IsLive: true };
                double due = _previewDue.GetValueOrDefault(w.Handle);
                if (hasPreview && fps <= 0 && focus <= 0.5f) continue; // static: first frame only
                if (hasPreview && now < due) continue;
                if (TryConsume(source, (tex, cw, ch) => renderer.UpdatePreview(w.Handle, tex, cw, ch, _sessionQuality.PreviewMaxDimension)))
                    _previewDue[w.Handle] = fps > 0 ? now + 1.0 / fps : now;
            }
        }
    }

    private static bool TryConsume(CaptureSource source, Action<Vortice.Direct3D11.ID3D11Texture2D, int, int> use)
    {
        var frame = source.TryGetLatest();
        if (frame is null) return false;
        try
        {
            using var texture = CaptureInterop.GetTexture(frame.Surface);
            if (texture is null) return false;
            var size = frame.ContentSize;
            use(texture, size.Width, size.Height);
            source.LastFrameTimestamp = Stopwatch.GetTimestamp();
            return true;
        }
        finally
        {
            frame.Dispose();
            source.AfterFrame();
        }
    }

    // ═════════════════════════════════════ icons & accents ═════════════════════════════════════

    /// <summary>Called while idle when background work (icons) completed.</summary>
    public void PumpIdle()
    {
        if (_state == State.Idle) PumpIcons();
    }

    private void OnWindowDiscovered(WindowInfo window)
    {
        _icons.Request(window);
        ApplyAccent(window);
    }

    private void ApplyAccent(WindowInfo window)
    {
        if (_accents.TryGetValue(window.App.Id, out var accent))
        {
            window.Accent = accent;
            return;
        }
        if (KnownAppColors.TryGet(window.App.ExecutableName, window.App.AppUserModelId, out var known))
            window.Accent = AccentExtractor.NormalizeForGlow(known);
    }

    private void PumpIcons()
    {
        while (_icons.TryDequeue(out var icon))
        {
            _accents[icon.AppId] = icon.Accent;
            if (icon.DisplayName is { } name) _identities.RefineDisplayName(icon.AppId, name);
            try
            {
                _renderer?.UploadIcon(icon);
            }
            catch (Exception ex)
            {
                Log.Debug($"Icon upload failed: {ex.Message}");
            }
            if (_session is not null)
            {
                foreach (var entry in _session.Entries)
                    foreach (var w in entry.Windows)
                        if (w.App.Id == icon.AppId) w.Accent = icon.Accent;
            }
        }
    }

    // ═════════════════════════════════════ pointer ═════════════════════════════════════

    private void OnPointer(PointerEvent e)
    {
        var s = _session;
        if (s is null || _state != State.Visible) return;
        switch (e.Kind)
        {
            case PointerEventKind.Move:
                _pointer = e.Position;
                if (Vector2.Distance(e.Position, _pointerAtReveal) > 24f) s.Expand();
                break;
            case PointerEventKind.Leave:
                _pointer = null;
                break;
            case PointerEventKind.Down:
                _pressed = _composer.HitTest(e.Position);
                break;
            case PointerEventKind.Up:
                var hit = _composer.HitTest(e.Position);
                if (hit.Kind != _pressed.Kind || hit.Key != _pressed.Key || hit.SubIndex != _pressed.SubIndex) break;
                switch (hit.Kind)
                {
                    case HitKind.Card when hit.Key is not null:
                        s.CommitEntry(hit.Key);
                        break;
                    case HitKind.Satellite when hit.Key is not null:
                        s.CommitEntry(hit.Key, hit.SubIndex);
                        break;
                    case HitKind.CloseButton when hit.Key is not null:
                        s.CloseEntry(hit.Key);
                        break;
                    case HitKind.PinButton when hit.Key is not null:
                        s.TogglePin(hit.Key);
                        break;
                    case HitKind.Desktop:
                        s.SwitchDesktop(hit.Desktop);
                        break;
                    case HitKind.Background when s.Mode == SessionMode.Sticky:
                        s.Cancel();
                        break;
                }
                break;
            case PointerEventKind.Wheel:
                if (!_settings.Hotkeys.WheelNavigation) break;
                _wheelAccumulator += e.WheelDelta;
                while (Math.Abs(_wheelAccumulator) >= 120)
                {
                    int step = _wheelAccumulator > 0 ? -1 : 1;
                    _wheelAccumulator += step * 120;
                    s.Move(step);
                }
                break;
        }
    }

    private static string? HoverKey(HitResult hit) => hit.Kind is HitKind.Card or HitKind.CloseButton or HitKind.PinButton ? hit.Key : null;

    private Vector2? GetCursorOnOverlay()
    {
        if (_primary is null || !GetCursorPos(out var pt)) return null;
        return new Vector2(pt.X - _primary.Bounds.Left, pt.Y - _primary.Bounds.Top);
    }

    // ═════════════════════════════════════ monitors ═════════════════════════════════════

    private MonitorDescriptor? PickMonitor() => _settings.General.ShowOn switch
    {
        MonitorPlacement.MouseCursor => Monitors.ForCursor(),
        _ => Monitors.ForWindow(GetForegroundWindow()),
    } ?? Monitors.Primary();

    private void PlaceOverlays()
    {
        _monitor = PickMonitor();
        if (_monitor is not null) _primary!.SetBounds(_monitor.Bounds);

        foreach (var o in _secondary) o.Dispose();
        _secondary.Clear();
        if (_settings.General.ShowOn == MonitorPlacement.AllMonitors && _gfx is not null)
        {
            foreach (var m in Monitors.All())
            {
                if (_monitor is not null && m.Handle == _monitor.Handle) continue;
                try
                {
                    _secondary.Add(new OverlaySurface(_gfx, m.Bounds));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Secondary overlay failed: {ex.Message}");
                }
            }
        }
    }

    private void UpdateDesktopInfo(IReadOnlyList<WindowInfo> windows)
    {
        if (_input is null) return;
        if (_desktopList.Count <= 1)
        {
            _input.Desktops = Array.Empty<DesktopInfo>();
            return;
        }
        var counts = windows.GroupBy(w => w.IsOnCurrentDesktop ? Guid.Empty : _desktops.GetDesktopId((nint)w.Handle))
            .ToDictionary(g => g.Key, g => g.Count());
        _input.Desktops = _desktopList
            .Select(d => new DesktopInfo(d.Name, d.IsCurrent ? counts.GetValueOrDefault(Guid.Empty) : counts.GetValueOrDefault(d.Id), d.IsCurrent))
            .ToList();
    }

    // ═════════════════════════════════════ quick-switch highlight ═════════════════════════════════════

    private void StartFlash(WindowInfo target)
    {
        if (_primary is null || target.Bounds.IsEmpty) return;
        var monitor = Monitors.ForWindow((nint)target.Handle);
        if (monitor is null) return;
        _primary.SetBounds(monitor.Bounds);
        var b = target.Bounds;
        _flashRect = new Vector4(b.X - monitor.Bounds.Left, b.Y - monitor.Bounds.Top, b.Width, b.Height);
        _flashAccent = target.Accent;
        _flashStart = Now;
        _state = State.Flash;
        OverlayVisible = true;
    }

    private void RenderFlash(double now)
    {
        const double duration = 0.34;
        float t = (float)((now - _flashStart) / duration);
        if (t >= 1f || _primary is null)
        {
            _primary?.Hide();
            OverlayVisible = false;
            _state = State.Idle;
            if (_gfx is not null)
            {
                _gfx.Context.ClearState();
                _gfx.Trim();
            }
            return;
        }
        float scale = DesignSpace.For(new Vector2(_primary.Width, _primary.Height)).Scale;
        var list = _composer.ComposeFlash(new Vector2(_primary.Width, _primary.Height), _flashRect, _flashAccent, t, scale);
        _renderer!.Render(_primary.RenderTarget, _primary.Width, _primary.Height, list, useBackdrop: false);
        _primary.Present(vsync: true);
        if (!_primary.IsVisible) _primary.Show();
    }

    // ═════════════════════════════════════ policy ═════════════════════════════════════

    private void OnForegroundChanged(nint hwnd) => UpdatePassthrough(hwnd);

    /// <summary>Keeps native Alt+Tab for fullscreen games and user-listed apps.</summary>
    private void UpdatePassthrough(nint foreground)
    {
        bool passthrough = false;
        if (_settings.Advanced.NativeInFullscreenGames && SystemPreferences.IsFullscreenGameRunning()) passthrough = true;
        else if (_settings.Advanced.PassthroughProcesses.Count > 0 && SystemPreferences.GetProcessName(foreground) is { } exe)
            passthrough = _settings.Advanced.PassthroughProcesses.Any(p => string.Equals(NormalizeExe(p), exe, StringComparison.OrdinalIgnoreCase));
        _bridge.PassthroughForeground = passthrough;
    }

    /// <summary>Drops effects for the rest of the session if frames keep missing the display's cadence.</summary>
    private void TrackFrameTime(float dt)
    {
        if (!_settings.Performance.AdaptiveQuality || _monitor is null || _sessionQuality.Preset == QualityPreset.BatterySaver) return;
        double budget = 1.0 / Math.Max(30, _monitor.RefreshRate);
        _frameSamples++;
        _slowFrameTime = _slowFrameTime * 0.92 + (dt > budget * 1.6 ? 1 : 0) * 0.08;
        if (_frameSamples > 45 && _slowFrameTime > 0.5)
        {
            _sessionQuality = _sessionQuality.Degraded();
            _frameSamples = 0;
            _slowFrameTime = 0;
            Log.Info("Frames are running long; reducing effects for this session.");
        }
    }

    // ═════════════════════════════════════ self-test ═════════════════════════════════════

    /// <summary>The switcher-thread half of the self-test (Settings → Diagnostics → Run diagnostics).</summary>
    public void RunSelfTest(SelfTestReport report, SelfTestContext context)
    {
        try
        {
            var windows = _tracker.Snapshot();
            report.Add("Window enumeration", windows.Count > 0 ? CheckResult.Pass : CheckResult.Warn,
                $"{windows.Count} switchable windows" + (windows.Count > 0
                    ? ": " + string.Join(", ", windows.Take(8).Select(w => w.App.ExecutableName)) + (windows.Count > 8 ? ", …" : string.Empty)
                    : " (open a few apps and run again)"));
        }
        catch (Exception ex)
        {
            report.Add("Window enumeration", CheckResult.Fail, ErrorText.Of(ex));
        }

        if (!GraphicsAvailable && _state == State.Idle) InitializeGraphics("self-test");

        report.Add("D3D11", HostHealth.D3D switch
        {
            ComponentState.Ready when _gfx?.IsWarp == true => CheckResult.Warn,
            ComponentState.Ready => CheckResult.Pass,
            ComponentState.Failed => CheckResult.Fail,
            _ => CheckResult.Skip,
        }, HostHealth.D3D == ComponentState.Ready
            ? $"adapter {HostHealth.Adapter}{(_gfx is not null ? ", feature level " + _gfx.Device.FeatureLevel : string.Empty)}{(_gfx?.IsWarp == true ? " - software (WARP) renderer, expect it to be slow" : string.Empty)}"
            : HostHealth.RendererError ?? "device not created");

        bool fallback = HostHealth.Composition == ComponentState.Failed && _primary is { IsOpaque: true };
        report.Add("DirectComposition", HostHealth.Composition switch
        {
            ComponentState.Ready => CheckResult.Pass,
            ComponentState.Failed when fallback => CheckResult.Warn,
            ComponentState.Failed => CheckResult.Fail,
            _ => CheckResult.Skip,
        }, HostHealth.Composition == ComponentState.Ready
            ? "device, target, visual, swap chain content and commit all succeeded"
            : fallback ? $"{_gfx?.CompositionError} - using an opaque window swap chain instead (works, without see-through)"
            : HostHealth.Composition == ComponentState.Failed ? _gfx?.CompositionError ?? HostHealth.OverlayError ?? HostHealth.RendererError : "not reached");

        report.Add("Shader loading", HostHealth.Shaders switch
        {
            ComponentState.Ready => CheckResult.Pass,
            ComponentState.Failed => CheckResult.Fail,
            _ => CheckResult.Skip,
        }, HostHealth.Shaders == ComponentState.Ready ? $"10 entry points from embedded {ShaderLibrary.ResourceName}" : HostHealth.ShaderError);

        if (_primary is { } overlay)
        {
            uint ex = GetExStyle(overlay.Handle);
            const uint required = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            bool stylesOk = (ex & required) == required;
            var monitor = Monitors.Primary();
            GetWindowRect(overlay.Handle, out RECT rect);
            bool exists = IsWindow(overlay.Handle);
            report.Add("Overlay HWND", exists && stylesOk ? CheckResult.Pass : CheckResult.Fail,
                $"HWND 0x{overlay.Handle:X}, exstyle 0x{ex:X8} (topmost {(ex & WS_EX_TOPMOST) != 0}, toolwindow {(ex & WS_EX_TOOLWINDOW) != 0}, " +
                $"noactivate {(ex & WS_EX_NOACTIVATE) != 0}), window rect {rect}, target {overlay.Bounds}, primary monitor {monitor?.Bounds}");
            report.Add("Test frame", RenderTestFrame(overlay, out string frameDetail), frameDetail);
        }
        else
        {
            report.Add("Overlay HWND", CheckResult.Fail, HostHealth.OverlayError ?? HostHealth.RendererError ?? "not created");
            report.Add("Test frame", CheckResult.Skip, "no overlay");
        }

        report.Add("WinRT projection", HostHealth.WinRtProjectionBroken ? CheckResult.Fail : CheckResult.Pass,
            HostHealth.WinRtProjection ?? CaptureProbe.CheckProjection(out _));
        context.WinRtDevice = _gfx?.WinRTDevice;
        context.Monitor = Monitors.Primary()?.Handle ?? 0;
    }

    private bool RenderTestFrame(OverlaySurface overlay, out string detail)
    {
        if (_state != State.Idle)
        {
            detail = "skipped: a switcher session is open";
            return true;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            float scale = DesignSpace.For(new Vector2(overlay.Width, overlay.Height)).Scale;
            var rect = new Vector4(overlay.Width * 0.25f, overlay.Height * 0.25f, overlay.Width * 0.5f, overlay.Height * 0.5f);
            var list = _composer.ComposeFlash(new Vector2(overlay.Width, overlay.Height), rect, new ColorF(0.36f, 0.42f, 1f, 1f), 0.5f, scale);
            _renderer!.Render(overlay.RenderTarget, overlay.Width, overlay.Height, list, useBackdrop: false);
            bool presented = overlay.Present(vsync: false);
            _gfx!.Context.Flush();
            if (_gfx.IsLost)
            {
                detail = $"device removed after the test frame: {_gfx.DeviceRemovedReason}";
                return false;
            }
            detail = presented
                ? $"rendered {list.Commands.Length} draw commands at {overlay.Width}×{overlay.Height} and presented in {sw.Elapsed.TotalMilliseconds:0.0} ms (window stays hidden)"
                : $"Present failed: {overlay.LastPresentError}";
            return presented;
        }
        catch (Exception ex)
        {
            detail = ErrorText.Of(ex);
            Log.Error("Self-test frame failed", ex);
            return false;
        }
        finally
        {
            _gfx?.Context.ClearState();
        }
    }

    // ═════════════════════════════════════ teardown ═════════════════════════════════════

    private void DisposeGraphics()
    {
        _bridge.RendererReady = false;
        if (HostHealth.Overlay == ComponentState.Ready) HostHealth.Overlay = ComponentState.NotStarted;
        if (HostHealth.Capture == ComponentState.Ready) HostHealth.Capture = ComponentState.NotStarted;
        HostHealth.OverlayHwnd = 0;
        foreach (var o in _secondary) o.Dispose();
        _secondary.Clear();
        _primary?.Dispose();
        _primary = null;
        _capture?.Dispose();
        _capture = null;
        _renderer?.Dispose();
        _renderer = null;
        _gfx?.Dispose();
        _gfx = null;
    }

    public void Dispose()
    {
        FinishSession("shutdown");
        _tracker.Dispose();
        _icons.Dispose();
        _desktops.Dispose();
        DisposeGraphics();
    }
}
