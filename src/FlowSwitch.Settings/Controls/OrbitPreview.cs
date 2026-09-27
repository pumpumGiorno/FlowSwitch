using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Model;
using FlowSwitch.Core.Scene;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace FlowSwitch.Settings.Controls;

/// <summary>
/// A live miniature of the switcher. It runs the real engine — the same session, springs and
/// layouts as the overlay — against a handful of sample windows, and redraws with WPF primitives.
/// Every settings change is therefore visible immediately, with the exact motion the user will get.
/// </summary>
public sealed class OrbitPreview : FrameworkElement
{
    public static readonly DependencyProperty SettingsProperty =
        DependencyProperty.Register(nameof(Settings), typeof(FlowSwitchSettings), typeof(OrbitPreview),
            new PropertyMetadata(null, (d, _) => ((OrbitPreview)d).Refresh(force: true)));

    /// <summary>Shows this mode instead of the configured one (used by onboarding).</summary>
    public static readonly DependencyProperty ModeOverrideProperty =
        DependencyProperty.Register(nameof(ModeOverride), typeof(SwitcherMode?), typeof(OrbitPreview),
            new PropertyMetadata(null, (d, _) => ((OrbitPreview)d).Refresh(force: true)));

    public static readonly DependencyProperty WindowCountProperty =
        DependencyProperty.Register(nameof(WindowCount), typeof(int), typeof(OrbitPreview),
            new PropertyMetadata(7, (d, _) => ((OrbitPreview)d).Refresh(force: true)));

    public static readonly DependencyProperty AutoPlayProperty =
        DependencyProperty.Register(nameof(AutoPlay), typeof(bool), typeof(OrbitPreview), new PropertyMetadata(true));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(OrbitPreview),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly (string App, string Exe, string Title, int W, int H)[] Samples =
    {
        ("Visual Studio Code", "Code.exe", "OrbitPreview.cs — FlowSwitch", 1920, 1080),
        ("Discord", "Discord.exe", "#design", 1600, 1000),
        ("Google Chrome", "chrome.exe", "Orbital mechanics — YouTube", 1920, 1080),
        ("Spotify", "Spotify.exe", "Spotify Premium", 1500, 950),
        ("Telegram", "Telegram.exe", "Telegram", 1280, 860),
        ("Figma", "Figma.exe", "FlowSwitch — Orbit exploration", 1920, 1080),
        ("Windows Terminal", "WindowsTerminal.exe", "pwsh", 1300, 800),
        ("Steam", "steam.exe", "Steam", 1600, 1000),
        ("Photoshop", "Photoshop.exe", "Nebula_key_visual.psd", 1920, 1080),
        ("File Explorer", "explorer.exe", "Downloads", 1400, 900),
        ("Notepad", "notepad.exe", "ideas.txt", 1100, 800),
        ("Obsidian", "Obsidian.exe", "Motion language", 1500, 950),
    };

    // The autoplay script: single steps, a quick burst (shows inertia), and a step back.
    private static readonly int[][] Script = { new[] { 1 }, new[] { 1 }, new[] { 1, 1, 1 }, new[] { -1 }, new[] { 1 }, new[] { -1, -1 } };

    private readonly SampleResources _resources = new();
    private readonly Dictionary<(string, double, int), FormattedText> _text = new();
    private readonly LayoutContext _ctx = new();
    private SwitcherSession? _session;
    private readonly SwitcherAnimator _animator = new();
    private ILayoutEngine _engine = LayoutFactory.Create(SwitcherMode.SolarSystem);
    private ScenePalette _palette = new();
    private SwitcherMode _mode;
    private GroupingMode _grouping = (GroupingMode)(-1);
    private int _windowCount = -1;
    private bool _rendering;
    private TimeSpan _lastFrame;
    private double _configAge;
    private double _untilStep = 1.6;
    private int _scriptIndex;
    private const double BurstInterval = 0.085;
    private readonly Queue<int> _pending = new();
    private double _pendingDelay;
    private double _pauseAutoplay;
    private Vector2? _pointer;

    public OrbitPreview()
    {
        ClipToBounds = true;
        Focusable = false;
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
    }

    public FlowSwitchSettings? Settings
    {
        get => (FlowSwitchSettings?)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public SwitcherMode? ModeOverride
    {
        get => (SwitcherMode?)GetValue(ModeOverrideProperty);
        set => SetValue(ModeOverrideProperty, value);
    }

    public int WindowCount
    {
        get => (int)GetValue(WindowCountProperty);
        set => SetValue(WindowCountProperty, value);
    }

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Plays the opening animation again (e.g. after changing the animation preset).</summary>
    public void Replay()
    {
        if (_session is null) return;
        _animator.Begin(_session, _ctx.Motion);
        _untilStep = 1.4;
    }

    // ───────────────────────────── engine ─────────────────────────────

    private void Refresh(bool force)
    {
        var settings = Settings ?? new FlowSwitchSettings();
        var mode = ModeOverride ?? settings.General.Mode;
        bool reducedBySystem = !SystemParameters.ClientAreaAnimation;
        var motion = MotionProfile.Resolve(settings.Animation, settings.Solar, reducedBySystem);

        _palette = ScenePalette.Resolve(settings.Appearance, settings.Solar, mode);
        _ctx.Solar = settings.Solar;
        _ctx.Motion = motion;
        _ctx.CardSize = settings.Appearance.CardSize;

        if (mode != _mode || force)
        {
            _mode = mode;
            _ctx.Mode = mode;
            _engine = LayoutFactory.Create(mode);
        }

        int count = Math.Clamp(WindowCount, 2, Samples.Length);
        if (_session is null || count != _windowCount || settings.General.Grouping != _grouping)
        {
            _windowCount = count;
            _grouping = settings.General.Grouping;
            var windows = CreateWindows(count);
            _resources.Set(windows);
            _session = new SwitcherSession(windows, new SessionOptions
            {
                Mode = SessionMode.Sticky,
                Grouping = _grouping,
                ExpandDelay = 0.6f,
                SearchEnabled = false,
            });
            _session.Expand();
            _animator.Begin(_session, motion);
        }
    }

    private static List<WindowInfo> CreateWindows(int count)
    {
        var list = new List<WindowInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var (app, exe, title, w, h) = Samples[i];
            KnownAppColors.TryGet(exe, null, out var accent);
            if (accent == default) accent = ColorF.NeutralAccent;
            list.Add(new WindowInfo
            {
                Handle = 0x1000 + i * 0x10,
                App = new AppIdentity(exe.ToLowerInvariant(), app, exe),
                Title = title,
                Bounds = new RectI(0, 0, w, h),
                Accent = AccentExtractor.NormalizeForGlow(accent),
                IsMinimized = exe == "steam.exe",
            });
        }
        return list;
    }

    private void UpdateRendering()
    {
        if (IsLoaded && IsVisible) StartRendering();
        else StopRendering();
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        Refresh(force: _session is null);
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        if (now == _lastFrame) return; // Rendering can fire more than once per frame.
        float dt = _lastFrame == TimeSpan.Zero ? 1f / 60f : (float)Math.Clamp((now - _lastFrame).TotalSeconds, 0.0, 0.1);
        _lastFrame = now;

        _configAge += dt;
        if (_configAge > 0.2)
        {
            _configAge = 0;
            Refresh(force: false);
        }
        if (_session is null || ActualWidth < 10 || ActualHeight < 10) return;

        StepAutoplay(dt);
        _session.Tick(dt);
        while (_session.TryDequeueEffect(out _)) { }

        _ctx.Space = DesignSpace.For(new Vector2((float)ActualWidth, (float)ActualHeight));
        bool parallax = (Settings?.Solar.Parallax ?? true) && !_ctx.Motion.Reduced;
        _animator.Update(dt, _session, _engine, _ctx, _resources, _palette, parallax ? _pointer : null);
        InvalidateVisual();
    }

    private void StepAutoplay(float dt)
    {
        if (_session is null) return;
        if (_pauseAutoplay > 0)
        {
            _pauseAutoplay -= dt;
            return;
        }
        if (_pending.Count > 0)
        {
            _pendingDelay -= dt;
            if (_pendingDelay <= 0)
            {
                _session.Move(_pending.Dequeue());
                _pendingDelay = BurstInterval;
            }
            return;
        }
        if (!AutoPlay) return;
        _untilStep -= dt;
        if (_untilStep > 0) return;

        var steps = Script[_scriptIndex++ % Script.Length];
        foreach (int delta in steps) _pending.Enqueue(delta);
        _pendingDelay = 0;
        _untilStep = 2.1 + steps.Length * 0.12;
    }

    // ───────────────────────────── input ─────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _pointer = new Vector2((float)(p.X / Math.Max(1, ActualWidth) * 2 - 1), (float)(p.Y / Math.Max(1, ActualHeight) * 2 - 1));
        _animator.HoveredKey = HitTestCard(p)?.Key;
        Cursor = _animator.HoveredKey is null ? null : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointer = null;
        _animator.HoveredKey = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_session is null) return;
        if (HitTestCard(e.GetPosition(this)) is { } card)
        {
            _session.SelectKey(card.Key);
            _pauseAutoplay = 4;
            _untilStep = 1.5;
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session is null) return;
        _session.Move(e.Delta > 0 ? -1 : 1);
        _pauseAutoplay = 4;
        _untilStep = 1.5;
        e.Handled = true;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    private CardVisual? HitTestCard(Point p)
    {
        CardVisual? best = null;
        float bestKey = float.MinValue;
        foreach (var card in _animator.Cards)
        {
            var pose = card.Pose;
            if (pose.Opacity < 0.2f) continue;
            var (tl, br) = Bounds(pose);
            if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y && pose.SortKey > bestKey)
            {
                best = card;
                bestKey = pose.SortKey;
            }
        }
        return best;
    }

    // ───────────────────────────── drawing ─────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width < 2 || size.Height < 2) return;
        var bounds = new Rect(size);
        dc.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));

        DrawBackdrop(dc, bounds);
        if (_session is not null && _animator.Layout.Poses.Length > 0)
        {
            DrawAmbient(dc);
            DrawRings(dc);
            var sorted = _animator.Cards.OrderBy(c => c.Pose.SortKey).ToList();
            foreach (var card in sorted) DrawGlow(dc, card);
            foreach (var card in sorted) DrawCard(dc, card);
        }

        dc.Pop();
        // Hairline frame, as on every glass surface in FlowSwitch.
        dc.DrawRoundedRectangle(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255))), 1),
            new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), CornerRadius, CornerRadius);
    }

    private void DrawBackdrop(DrawingContext dc, Rect bounds)
    {
        // A soft, out-of-focus "desktop": what the real overlay blurs behind itself.
        var wallpaper = new LinearGradientBrush(Color.FromRgb(0x1C, 0x2B, 0x4D), Color.FromRgb(0x2B, 0x1B, 0x3A), 35);
        dc.DrawRectangle(Frozen(wallpaper), null, bounds);
        DrawBlob(dc, bounds, 0.18, 0.28, 0.55, Color.FromRgb(0x3E, 0x6A, 0xB8), 0.55);
        DrawBlob(dc, bounds, 0.82, 0.22, 0.5, Color.FromRgb(0x8A, 0x4F, 0xA8), 0.45);
        DrawBlob(dc, bounds, 0.55, 0.95, 0.6, Color.FromRgb(0x2E, 0x8C, 0x8A), 0.35);

        float reveal = _animator.BackdropReveal;
        float dim = Math.Clamp(_palette.Dim * reveal, 0f, 1f);
        var baseTint = ToColor(_palette.BaseTint, dim);
        dc.DrawRectangle(Frozen(new SolidColorBrush(baseTint)), null, bounds);

        // Vignette.
        var vignette = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.45), GradientOrigin = new Point(0.5, 0.45), RadiusX = 0.8, RadiusY = 0.85,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45),
                new GradientStop(Color.FromArgb((byte)(120 * reveal), 0, 0, 0), 1.0),
            },
        };
        dc.DrawRectangle(Frozen(vignette), null, bounds);
    }

    private static void DrawBlob(DrawingContext dc, Rect bounds, double cx, double cy, double radius, Color color, double alpha)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        double r = radius * Math.Max(bounds.Width, bounds.Height) * 0.5;
        dc.DrawEllipse(Frozen(brush), null, new Point(bounds.Width * cx, bounds.Height * cy), r * 1.3, r);
    }

    private void DrawAmbient(DrawingContext dc)
    {
        var anchor = _animator.Layout.Anchor;
        var color = OkLab.ToSrgb(_animator.AmbientLab.Value);
        float strength = 0.34f * _palette.AmbientStrength * MathF.Max(0.35f, _palette.GlowStrength) * _animator.BackdropReveal;
        if (strength < 0.005f) return;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(ToColor(color, strength), 0),
                new GradientStop(ToColor(color, strength * 0.35f), 0.45),
                new GradientStop(ToColor(color, 0), 1),
            },
        };
        double r = _ctx.Space.Px(620f);
        dc.DrawEllipse(Frozen(brush), null, new Point(anchor.X, anchor.Y + _ctx.Space.Px(40f)), r * 1.25, r * 0.8);
    }

    private void DrawRings(DrawingContext dc)
    {
        var tint = ColorF.LerpOklab(ColorF.FromHex("#B8C6FF"), OkLab.ToSrgb(_animator.AmbientLab.Value), 0.3f);
        float appear = Easing.Smoothstep(0f, 0.4f, _animator.Time) * (1f - _animator.ExitProgress);
        foreach (var ring in _animator.Layout.Rings)
        {
            float alpha = _palette.OrbitAlpha * ring.Alpha * appear * 0.5f;
            if (alpha < 0.004f) continue;
            var pen = new Pen(Frozen(new SolidColorBrush(ToColor(tint, alpha))), Math.Max(1.0, _ctx.Space.Px(1.3f)));
            pen.Freeze();
            dc.PushTransform(new RotateTransform(ring.Roll * 180 / Math.PI, ring.Center.X, ring.Center.Y));
            dc.DrawEllipse(null, pen, new Point(ring.Center.X, ring.Center.Y), ring.Radii.X, ring.Radii.Y);
            dc.Pop();
        }
    }

    private void DrawGlow(DrawingContext dc, CardVisual card)
    {
        var pose = card.Pose;
        float intensity = _palette.GlowStrength * (0.12f + 0.5f * pose.Focus) * pose.Glow * pose.Opacity * (1f + 0.35f * card.Hover.Value);
        if (intensity < 0.004f || pose.PreviewSize.X < 1f) return;
        var accent = card.Accent;
        float gain = SceneComposer.GlowGain(accent);
        float spread = Easing.Lerp(1.15f, 1.95f, _palette.GlowRadius);
        var center = ProjectPoint(pose, new Vector2(0f, pose.PreviewSize.Y * 0.1f));
        double rx = pose.PreviewSize.X * 0.5 * spread + _ctx.Space.Px(60f) * Math.Max(pose.Scale, 0.35f);
        double ry = pose.PreviewSize.Y * 0.5 * spread + _ctx.Space.Px(70f) * Math.Max(pose.Scale, 0.35f);
        float a = Math.Clamp(intensity * gain * 0.62f, 0f, 1f);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(ToColor(accent, a), 0),
                new GradientStop(ToColor(accent, a * 0.42f), 0.4),
                new GradientStop(ToColor(accent, 0), 1),
            },
        };
        dc.DrawEllipse(Frozen(brush), null, center, rx, ry);
    }

    private void DrawCard(DrawingContext dc, CardVisual card)
    {
        var pose = card.Pose;
        if (pose.Opacity < 0.004f || pose.PreviewSize.X < 1f) return;
        var window = card.Entry.Primary;
        var accent = card.Accent;
        bool orbital = LayoutFactory.IsOrbital(_mode);
        float s = _ctx.Space.Scale;
        float k = MathF.Max(pose.Scale, 0.05f);
        float hoverPop = 1f + 0.028f * card.Hover.Value * _ctx.Motion.ScaleIntensity;

        float pad = SolarSystemLayout.CardPadding * s * k;
        float infoH = (orbital ? SolarSystemLayout.InfoStripHeight * s * k : 0f) * pose.InfoAlpha;
        var ph = pose.PreviewSize * 0.5f * hoverPop;
        var outerHalf = new Vector2(ph.X + pad, ph.Y + pad + infoH * 0.5f);
        var outerOffset = new Vector2(0f, infoH * 0.5f);
        double radius = _palette.CornerRadius * s * k * hoverPop;

        dc.PushOpacity(pose.Opacity);

        // Floor reflection (Cover Flow / Carousel).
        if (pose.Reflection > 0.001f)
        {
            float gap = 6f * s;
            var reflectCenter = new Vector2(0f, (outerHalf.Y + outerOffset.Y) * 2f + gap);
            var reflect = Quad(pose, outerHalf, outerOffset + reflectCenter);
            var fade = new LinearGradientBrush(ToColor(ColorF.LerpOklab(_palette.GlassTint, accent, 0.25f), pose.Reflection * 0.55f),
                ToColor(_palette.GlassTint, 0), 90);
            DrawQuad(dc, reflect, radius, Frozen(fade), null);
        }

        // Glass frame.
        var outer = Quad(pose, outerHalf, outerOffset);
        var glassTop = ColorF.LerpOklab(_palette.GlassTint, accent, 0.14f);
        var glass = new LinearGradientBrush(ToColor(glassTop, _palette.GlassOpacity), ToColor(_palette.GlassTint, _palette.GlassOpacity * 0.92f), 90);
        var edge = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * Math.Clamp(0.16f + 0.3f * _palette.BorderStrength + 0.25f * pose.Focus, 0f, 1f)), 255, 255, 255),
            Color.FromArgb((byte)(255 * 0.06f), 255, 255, 255), 90);
        var edgePen = new Pen(Frozen(edge), Math.Max(1.0, s * 1.2));
        edgePen.Freeze();
        DrawQuad(dc, outer, radius, Frozen(glass), edgePen);

        // Preview area: a stylised window in the app's colours, or the fallback icon when minimised.
        var inner = Quad(pose, ph, Vector2.Zero);
        double innerRadius = Math.Max(2 * s, radius - pad * 0.75);
        if (window.IsMinimized) DrawFallback(dc, inner, innerRadius, accent, window, ph);
        else DrawWindowMock(dc, pose, inner, innerRadius, accent, ph, (int)(window.Handle >> 4));

        // Info strip: icon, app name and title under the focused card.
        if (orbital && pose.InfoAlpha > 0.02f)
        {
            dc.PushOpacity(pose.InfoAlpha);
            float icon = 30f * s * k;
            var iconCenter = ProjectPoint(pose, new Vector2(-ph.X + 2f * s * k + icon * 0.5f, ph.Y + pad + 28f * s * k));
            DrawAppBadge(dc, iconCenter, icon, accent, window.App.DisplayName);
            double fontName = 15 * s * k * _palette.FontScale;
            double fontTitle = 12 * s * k * _palette.FontScale;
            var name = Text(window.App.DisplayName, fontName, 600, TextPrimary);
            var title = Text(window.Title, fontTitle, 400, TextSecondary);
            double maxW = Math.Max(10, ph.X * 2 - icon - 14 * s * k);
            name.MaxTextWidth = maxW;
            title.MaxTextWidth = maxW;
            name.MaxLineCount = 1;
            title.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            title.Trimming = TextTrimming.CharacterEllipsis;
            double x = iconCenter.X + icon * 0.5 + 12 * s * k;
            dc.DrawText(name, new Point(x, iconCenter.Y - name.Height + 1 * s));
            dc.DrawText(title, new Point(x, iconCenter.Y + 2 * s));
            dc.Pop();
        }
        else if (pose.Focus < 0.5f && k > 0.2f && !window.IsMinimized)
        {
            // Corner badge on orbiting cards.
            float badge = MathF.Max(16f * s, 26f * s * k);
            var at = ProjectPoint(pose, new Vector2(-ph.X + badge * 0.42f, ph.Y - badge * 0.05f));
            DrawAppBadge(dc, at, badge, accent, window.App.DisplayName);
        }

        // Group count.
        if (card.Entry.IsGroup)
        {
            var countText = Text("×" + card.Entry.Windows.Count.ToString(CultureInfo.CurrentCulture), 11 * s * Math.Max(k, 0.6), 600, TextPrimary);
            var at = ProjectPoint(pose, new Vector2(ph.X - 8f * s * k, -ph.Y + 8f * s * k));
            dc.DrawText(countText, new Point(at.X - countText.Width, at.Y));
        }

        // Depth: farther cards are darker (the real renderer also blurs them).
        float shade = Math.Clamp((1f - pose.Brightness) * 0.95f + pose.Blur * 0.12f, 0f, 0.85f);
        if (shade > 0.01f) DrawQuad(dc, outer, radius, Frozen(new SolidColorBrush(Color.FromArgb((byte)(255 * shade), 6, 8, 14))), null);

        dc.Pop();
    }

    private void DrawWindowMock(DrawingContext dc, in CardPose pose, Point[] quad, double radius, ColorF accent, Vector2 ph, int seed)
    {
        var dark = ColorF.LerpOklab(ColorF.FromHex("#12151F"), accent, 0.10f);
        var light = ColorF.LerpOklab(ColorF.FromHex("#1B2030"), accent, 0.22f);
        var fill = new LinearGradientBrush(ToColor(light, 1f), ToColor(dark, 1f), 90);
        DrawQuad(dc, quad, radius, Frozen(fill), null);

        // Title bar and a few content blocks, laid out deterministically per window.
        float w = ph.X * 2, h = ph.Y * 2;
        var bar = Quad(pose, new Vector2(ph.X, h * 0.05f), new Vector2(0f, -ph.Y + h * 0.05f));
        DrawQuad(dc, bar, 0, Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0))), null);

        var rng = new Random(seed);
        var accentBrush = Frozen(new SolidColorBrush(ToColor(accent, 0.55f)));
        var soft = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));
        var softer = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255)));

        // Sidebar.
        bool sidebar = rng.NextDouble() > 0.35;
        float left = -ph.X + w * 0.05f;
        if (sidebar)
        {
            var side = Quad(pose, new Vector2(w * 0.11f, ph.Y - h * 0.1f), new Vector2(-ph.X + w * 0.11f, h * 0.05f));
            DrawQuad(dc, side, radius * 0.3, softer, null);
            left = -ph.X + w * 0.27f;
        }
        float right = ph.X - w * 0.05f;
        float y = -ph.Y + h * 0.18f;
        // Hero block in the app colour.
        float heroH = h * (0.18f + (float)rng.NextDouble() * 0.12f);
        DrawQuad(dc, Quad(pose, new Vector2((right - left) * 0.5f, heroH * 0.5f), new Vector2((left + right) * 0.5f, y + heroH * 0.5f)),
            radius * 0.35, accentBrush, null);
        y += heroH + h * 0.07f;
        for (int i = 0; i < 4 && y < ph.Y - h * 0.08f; i++)
        {
            float lineW = (right - left) * (0.45f + (float)rng.NextDouble() * 0.5f);
            float lineH = h * 0.045f;
            DrawQuad(dc, Quad(pose, new Vector2(lineW * 0.5f, lineH * 0.5f), new Vector2(left + lineW * 0.5f, y + lineH * 0.5f)),
                lineH * 0.5, i == 0 ? soft : softer, null);
            y += lineH + h * 0.05f;
        }
    }

    private void DrawFallback(DrawingContext dc, Point[] quad, double radius, ColorF accent, WindowInfo window, Vector2 ph)
    {
        var top = ColorF.LerpOklab(ColorF.FromHex("#1A1F2E"), accent, 0.35f);
        var bottom = ColorF.LerpOklab(ColorF.FromHex("#10131C"), accent, 0.12f);
        DrawQuad(dc, quad, radius, Frozen(new LinearGradientBrush(ToColor(top, 1f), ToColor(bottom, 1f), 90)), null);
        var center = new Point((quad[0].X + quad[2].X) * 0.5, (quad[0].Y + quad[2].Y) * 0.5);
        double size = Math.Min(Math.Abs(quad[1].X - quad[0].X), Math.Abs(quad[3].Y - quad[0].Y)) * 0.42;
        DrawAppBadge(dc, center, size, accent, window.App.DisplayName);
    }

    private void DrawAppBadge(DrawingContext dc, Point center, double size, ColorF accent, string name)
    {
        if (size < 3) return;
        var fill = new LinearGradientBrush(ToColor(ColorF.LerpOklab(accent, ColorF.White, 0.15f), 1f), ToColor(ColorF.LerpOklab(accent, ColorF.Black, 0.2f), 1f), 90);
        double r = size * 0.24;
        dc.DrawRoundedRectangle(Frozen(fill), null, new Rect(center.X - size / 2, center.Y - size / 2, size, size), r, r);
        string letter = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        var text = Text(letter, size * 0.52, 700, Color.FromArgb(0xF0, 0x0A, 0x0D, 0x14));
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    // ───────────────────────────── geometry ─────────────────────────────

    private FrameConstants Frame()
    {
        bool deep = _mode is SwitcherMode.Carousel or SwitcherMode.CoverFlow;
        var vp = _ctx.Space.Viewport;
        var anchor = _animator.Layout.Anchor;
        return new FrameConstants { Camera = new Vector4(_animator.Time, vp.Y * (deep ? 1.35f : 1.9f), anchor.X, anchor.Y) };
    }

    private Point ProjectPoint(in CardPose pose, Vector2 local)
    {
        var frame = Frame();
        var p = Projection.Project(local, new Vector4(pose.Center, 1f, 1f), new Vector4(pose.Yaw, pose.Pitch, 0f, pose.Z), frame);
        return new Point(p.X, p.Y);
    }

    private Point[] Quad(in CardPose pose, Vector2 half, Vector2 offset)
    {
        var frame = Frame();
        var p0 = new Vector4(pose.Center, 1f, 1f);
        var p2 = new Vector4(pose.Yaw, pose.Pitch, 0f, pose.Z);
        var result = new Point[4];
        ReadOnlySpan<Vector2> corners = stackalloc Vector2[] { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };
        for (int i = 0; i < 4; i++)
        {
            var v = Projection.Project(offset + corners[i] * half, p0, p2, frame);
            result[i] = new Point(v.X, v.Y);
        }
        return result;
    }

    private (Point, Point) Bounds(in CardPose pose)
    {
        var q = Quad(pose, pose.PreviewSize * 0.5f + new Vector2(SolarSystemLayout.CardPadding * _ctx.Space.Scale * pose.Scale), Vector2.Zero);
        double minX = q.Min(p => p.X), maxX = q.Max(p => p.X), minY = q.Min(p => p.Y), maxY = q.Max(p => p.Y);
        return (new Point(minX, minY), new Point(maxX, maxY));
    }

    private static void DrawQuad(DrawingContext dc, Point[] q, double radius, Brush? fill, Pen? pen)
    {
        // Nearly axis-aligned quads keep their rounded corners; strongly rotated ones become polygons.
        bool aligned = Math.Abs(q[0].Y - q[1].Y) < 0.75 && Math.Abs(q[0].X - q[3].X) < 0.75;
        if (aligned)
        {
            var rect = new Rect(q[0], q[2]);
            double r = Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5);
            dc.DrawRoundedRectangle(fill, pen, rect, r, r);
            return;
        }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(q[0], fill is not null, true);
            ctx.PolyLineTo(new[] { q[1], q[2], q[3] }, true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, pen, geometry);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static readonly Color TextPrimary = Color.FromRgb(0xF2, 0xF4, 0xF8);
    private static readonly Color TextSecondary = Color.FromArgb(0xB0, 0xC8, 0xCF, 0xDD);
    private static readonly Typeface[] Faces = new Typeface[3];

    private FormattedText Text(string text, double size, int weight, Color color)
    {
        size = Math.Max(1, Math.Round(size * 2) / 2);
        var key = (text + "\u0001" + color, size, weight);
        if (_text.TryGetValue(key, out var cached)) return cached;
        if (_text.Count > 400) _text.Clear();
        int slot = weight >= 700 ? 2 : weight >= 600 ? 1 : 0;
        var face = Faces[slot] ??= new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
            slot == 2 ? FontWeights.Bold : slot == 1 ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size,
            Frozen(new SolidColorBrush(color)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _text[key] = formatted;
        return formatted;
    }

    private static Color ToColor(ColorF c, float alpha) =>
        Color.FromArgb((byte)(Math.Clamp(alpha, 0f, 1f) * 255), ToByte(c.R), ToByte(c.G), ToByte(c.B));

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    private sealed class SampleResources : ISceneResources
    {
        private Dictionary<long, WindowInfo> _windows = new();

        public void Set(IEnumerable<WindowInfo> windows) => _windows = windows.ToDictionary(w => w.Handle);

        public bool HasBackdrop => true;

        public PreviewInfo GetPreview(long windowHandle) =>
            _windows.TryGetValue(windowHandle, out var w) && !w.IsMinimized
                ? new PreviewInfo(true, new Vector4(0, 0, 1, 1), true, w.AspectRatio)
                : PreviewInfo.Missing;

        public bool HasIcon(string appId) => true;
    }
}
