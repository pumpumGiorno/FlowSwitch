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
    private bool _graphicsFailed;

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

    public bool GraphicsAvailable => _gfx is not null && !_graphicsFailed;

    public bool WantsFrames => _state != State.Idle || Now < _prewarmUntil;

    /// <summary>Handle the render loop waits on (0 → use a short timeout).</summary>
    public nint FrameWaitable => _state is State.Visible or State.Closing or State.Flash && _primary is { } p ? p.FrameLatencyWaitable : 0;

    private double Now => _clock.Elapsed.TotalSeconds;

    // ═════════════════════════════════════ setup ═════════════════════════════════════

    public bool InitializeGraphics()
    {
        if (_gfx is not null) return true;
        if (_graphicsFailed) return false;
        try
        {
            var sw = Stopwatch.StartNew();
            _gfx = GraphicsDevice.Create();
            _renderer = new Renderer(_gfx, _shaderCache);
            _capture = new CaptureService(_gfx.WinRTDevice);
            var monitor = Monitors.Primary();
            _primary = new OverlaySurface(_gfx, monitor?.Bounds ?? new RECT { Right = 1920, Bottom = 1080 });
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
            Log.Info($"Graphics ready in {sw.ElapsedMilliseconds} ms (live previews: {_capture.Supported}).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Graphics initialisation failed — FlowSwitch will leave Alt+Tab to Windows", ex);
            _graphicsFailed = true;
            DisposeGraphics();
            return false;
        }
    }

    public void ApplySettings(FlowSwitchSettings settings)
    {
        _settings = settings;
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
        Log.MinimumLevel = settings.Advanced.LogLevel;
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
                if ((int)lParam == _sessionId) _session?.Cancel();
                break;
            case HookMessages.Abort:
                if ((int)lParam == _sessionId) FinishSession(immediate: true);
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
    public void ShowPreview()
    {
        if (_state != State.Idle) return;
        int id = _bridge.BeginSession(sticky: true);
        Begin(BeginFlags.Sticky, id);
    }

    private void Begin(BeginFlags flags, int id)
    {
        // Answer the hook first: from here on the overlay is committed to this Alt+Tab.
        _bridge.Acknowledge(id);
        if (_state != State.Idle) FinishSession(immediate: true);
        if (!InitializeGraphics())
        {
            _bridge.EndSession(id);
            return;
        }

        RefreshProfiles();
        _sessionQuality = _quality;
        _frameSamples = 0;
        _slowFrameTime = 0;

        var windows = _tracker.Snapshot();
        foreach (var w in windows) ApplyAccent(w);

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
            _bridge.EndSession(id);
            return;
        }

        _session = session;
        _sessionId = id;
        _sessionStart = Now;
        _lastFrame = Now;
        _pointer = null;
        _pressed = default;
        _wheelAccumulator = 0;
        _backdropFrozen = false;

        PlaceOverlays();
        _layoutContext.Space = DesignSpace.For(new Vector2(_primary!.Width, _primary.Height));
        _animator.Begin(session, _motion);
        _input!.Palette = _palette;
        _input.Quality = _sessionQuality;
        _desktopList = _settings.General.ShowVirtualDesktops ? _desktops.GetDesktops() : Array.Empty<VirtualDesktop>();
        UpdateDesktopInfo(windows);

        _state = State.Pending;
        _revealAt = sticky ? Now : Now + _settings.General.RevealDelayMs / 1000.0;
        StartCaptures();
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Log.Debug($"Session {id} ({mode}) with {session.Entries.Count} entries.");
    }

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
                if (effect is ActivateWindowEffect a) WindowActivator.Activate(a.Window.Handle);
            FinishSession(immediate: true);
            if (target is not null && _settings.General.QuickSwitchHighlight && !_motion.Reduced) StartFlash(target);
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
                StartCaptures();
            }

            PumpCaptures(now);

            // Nothing animates while the overlay is still hidden: the entrance starts when it appears.
            if (_state == State.Pending) return;

            Vector2? normalized = null;
            if (_pointer is { } p && _primary is not null)
                normalized = new Vector2(p.X / _primary.Width * 2f - 1f, p.Y / _primary.Height * 2f - 1f);
            _animator.HoveredKey = _pointer is { } hp && _state == State.Visible ? HoverKey(_composer.HitTest(hp)) : null;
            _animator.Update(dt, s, _layout, _layoutContext, _renderer!, _palette, normalized);

            Draw(now, dt);
            // Show the windows only after their first frame is presented, so no stale content flashes.
            if (_primary is { IsVisible: false }) ShowOverlays();
            if (_animator.IsFinished) FinishSession(immediate: false);
        }
        catch (Exception ex)
        {
            Log.Error("Frame failed — closing the switcher", ex);
            FinishSession(immediate: true);
            if (_gfx?.IsLost == true)
            {
                Log.Warn("GPU device lost; graphics will be recreated on next use.");
                DisposeGraphics();
            }
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
        _primary.Present(vsync: true);

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
                    WindowActivator.Activate(a.Window.Handle);
                    break;
                case DismissEffect d:
                    _bridge.EndSession(_sessionId);
                    if (_state == State.Pending)
                    {
                        FinishSession(immediate: true);
                        return;
                    }
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

    /// <summary>The activated window's frame in overlay pixels, if it is on the overlay's monitor.</summary>
    private Vector4? ExitRect(WindowInfo? target)
    {
        if (target is null || _primary is null || target.Bounds.IsEmpty) return null;
        var b = target.Bounds;
        var m = _primary.Bounds;
        if (b.CenterX < m.Left || b.CenterX > m.Right || b.CenterY < m.Top || b.CenterY > m.Bottom) return null;
        return new Vector4(b.X - m.Left, b.Y - m.Top, b.Width, b.Height);
    }

    private void FinishSession(bool immediate)
    {
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
        _ = immediate;
    }

    // ═════════════════════════════════════ captures ═════════════════════════════════════

    private void Prewarm()
    {
        if (_state != State.Idle || !InitializeGraphics() || _capture is null || !_quality.Prewarm) return;
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

    // ═════════════════════════════════════ teardown ═════════════════════════════════════

    private void DisposeGraphics()
    {
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
        FinishSession(immediate: true);
        _tracker.Dispose();
        _icons.Dispose();
        _desktops.Dispose();
        DisposeGraphics();
    }
}
