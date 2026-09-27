using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Model;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Scene;

public enum HitKind
{
    None,
    Background,
    Card,
    Satellite,
    CloseButton,
    PinButton,
    Desktop,
}

public readonly record struct HitResult(HitKind Kind, string? Key = null, int SubIndex = -1, int Desktop = -1)
{
    public static HitResult Nothing => new(HitKind.None);
}

/// <summary>Everything the composer needs for one frame.</summary>
public sealed class SceneInput
{
    public required SwitcherSession Session { get; init; }
    public required SwitcherAnimator Animator { get; init; }
    public required LayoutContext Layout { get; init; }
    public required ScenePalette Palette { get; set; }
    public required QualityProfile Quality { get; set; }
    public required ISceneResources Resources { get; init; }
    public IReadOnlyList<DesktopInfo> Desktops { get; set; } = Array.Empty<DesktopInfo>();
    /// <summary>Pointer in overlay pixels, if the pointer is over the overlay.</summary>
    public Vector2? Pointer { get; set; }
    public int BackdropLevels { get; set; } = 6;
    public string? Stats { get; set; }
    public long FrameIndex { get; set; }
    public bool ShowHints { get; set; } = true;
}

/// <summary>
/// Turns the animated state into an ordered list of draw commands (the display list executed by
/// the Direct3D renderer, or by the design preview tool) and remembers hit regions for the mouse.
/// </summary>
/// <remarks>
/// Visual recipe (see docs/DESIGN.md): blurred, graded desktop → faint orbit lines and dust →
/// large soft accent lights → glass cards back-to-front (shadow, frosted body, live preview,
/// rim light, sheen, aura) → typography → chrome.
/// </remarks>
public sealed class SceneComposer
{
    private const string GlyphClose = "";
    private const string GlyphPin = "";
    private const string GlyphUnpin = "";
    private const string GlyphSearch = "";
    private const string GlyphDesktop = "";

    private readonly DrawList _list = new();
    private readonly List<HitRegion> _hits = new();
    private readonly List<(CardVisual Card, CardPose Pose, float Alpha, bool Ghost)> _sorted = new();
    private readonly Dictionary<(string, float, int, FontFamilyKind, float), TextSpec> _textCache = new();
    private FrameConstants _frame;
    private SceneInput _in = null!;
    private float _s;
    private float _live;
    private float _reveal;

    private enum HitShape { Quad, Circle }

    private readonly record struct HitRegion(HitKind Kind, string? Key, int Sub, int Desktop, HitShape Shape,
        Vector2 A, Vector2 B, Vector2 C, Vector2 D, float Radius);

    public DrawList Compose(SceneInput input)
    {
        _in = input;
        _list.Clear();
        _hits.Clear();

        var anim = input.Animator;
        var space = input.Layout.Space;
        var palette = input.Palette;
        _s = space.Scale;
        _live = 1f - anim.ExitProgress;
        _reveal = anim.BackdropReveal;

        bool deep = input.Layout.Mode is SwitcherMode.Carousel or SwitcherMode.CoverFlow;
        var anchor = anim.Layout.Anchor;
        _frame = new FrameConstants
        {
            Viewport = new Vector4(space.Viewport.X, space.Viewport.Y, 1f / space.Viewport.X, 1f / space.Viewport.Y),
            Camera = new Vector4(anim.Time, space.Viewport.Y * (deep ? 1.35f : 1.9f), anchor.X, anchor.Y),
            Backdrop = new Vector4(Math.Max(1, input.BackdropLevels - 1), 1.1f, palette.Grain, palette.Oled ? 1f : 0f),
            Globals = new Vector4(input.Resources.HasBackdrop ? 1f : 0f, palette.AdaptiveExposure ? 1f : 0f, 1f,
                                  input.FrameIndex % 64 / 64f),
        };
        _list.Frame = _frame;

        EmitBackdrop();
        if (LayoutFactory.IsOrbital(input.Layout.Mode))
        {
            EmitOrbits();
            EmitParticles();
        }
        EmitCards();
        EmitChrome();
        return _list;
    }

    // ───────────────────────────────── backdrop ─────────────────────────────────

    private void EmitBackdrop()
    {
        var anim = _in.Animator;
        var p = _in.Palette;
        var vp = _in.Layout.Space.Viewport;
        float fade = _reveal * _live;
        float maxLod = _frame.Backdrop.X;
        float ambient = 0.46f * p.AmbientStrength * MathF.Max(0.35f, p.GlowStrength) * fade;
        float idle = anim.Motion.IdleAmount;
        float shimmer = 1f + 0.06f * idle * MathF.Sin(anim.Time * MathF.Tau / 9f);
        var anchor = anim.Layout.Anchor;
        var parallax = anim.Parallax.Value * anim.Motion.ParallaxAmount * _s * -4f;

        ref var c = ref _list.Add(ShaderKind.Backdrop);
        c.P[0] = new Vector4(fade, maxLod * Easing.Lerp(0.45f, 0.95f, p.BlurAmount) * fade, p.Dim * fade, 0.5f * fade);
        var ambientColor = OkLab.ToSrgb(anim.AmbientLab.Value);
        c.P[1] = new Vector4(ScenePalette.Rgb(ambientColor), ambient * shimmer * GlowGain(ambientColor));
        c.P[2] = new Vector4(anchor + parallax, vp.X * 0.36f, vp.Y * 0.44f);
        c.P[3] = new Vector4(ScenePalette.Rgb(OkLab.ToSrgb(anim.AmbientPrevLab.Value)), ambient * 0.32f);
        c.P[4] = new Vector4(ScenePalette.Rgb(OkLab.ToSrgb(anim.AmbientNextLab.Value)), ambient * 0.32f);
        c.P[5] = new Vector4(anchor.X - vp.X * 0.34f, anchor.Y + vp.Y * 0.1f, anchor.X + vp.X * 0.34f, anchor.Y + vp.Y * 0.1f);
        c.P[6] = new Vector4(ScenePalette.Rgb(p.BaseTint), 0.35f);
        c.P[7] = new Vector4(p.Grain, p.AdaptiveExposure ? 1f : 0f, parallax.X, parallax.Y);
    }

    // ───────────────────────────────── orbits & dust ─────────────────────────────────

    private void EmitOrbits()
    {
        var anim = _in.Animator;
        var p = _in.Palette;
        if (p.OrbitAlpha <= 0.001f) return;
        float drawIn = anim.Motion.Reduced ? 1f : Easing.Enter.Evaluate(Math.Clamp(anim.Time / 0.55f, 0f, 1f));
        var tint = ColorF.LerpOklab(ColorF.FromHex("#B8C6FF"), OkLab.ToSrgb(anim.AmbientLab.Value), 0.3f);
        float highlight = (float)(Math.Clamp(anim.Rotor.Velocity, -6, 6) * 0.08);

        foreach (var ring in anim.Layout.Rings)
        {
            float alpha = p.OrbitAlpha * ring.Alpha * _live * Easing.Smoothstep(0f, 0.4f, anim.Time) * 0.55f;
            if (alpha <= 0.002f) continue;
            float thickness = MathF.Max(0.9f, 1.05f * _s);
            float glow = 9f * _s;
            ref var c = ref _list.Add(ShaderKind.Orbit);
            c.P[0] = new Vector4(ring.Center, 1f, alpha);
            c.P[1] = new Vector4(ring.Radii, glow + thickness + 2f, glow + thickness + 2f);
            c.P[2] = new Vector4(0f, 0f, ring.Roll, 0f);
            c.P[4] = new Vector4(ScenePalette.Rgb(tint), 1f);
            c.P[5] = new Vector4(ring.Radii, thickness, glow);
            c.P[6] = new Vector4(1f, p.Minimal ? 0.45f : 0.28f, drawIn, 0f);
            c.P[7] = new Vector4(p.Minimal ? 0f : 0.55f + MathF.Abs(highlight), 0f, anim.Time, highlight);
        }
    }

    private void EmitParticles()
    {
        var anim = _in.Animator;
        var p = _in.Palette;
        int count = (int)(_in.Quality.ParticleCount * Math.Clamp(p.ParticleAlpha * 2f, 0f, 1f));
        var rings = anim.Layout.Rings;
        if (count <= 0 || rings.Count == 0 || p.ParticleAlpha <= 0f) return;

        float fade = _reveal * _live * Easing.Smoothstep(0.1f, 0.6f, anim.Time);
        var tint = ColorF.LerpOklab(ColorF.FromHex("#DDE6FF"), OkLab.ToSrgb(anim.AmbientLab.Value), 0.35f);
        float motionScale = anim.Motion.Reduced ? 0f : 1f;
        float spin = (float)anim.Rotor.Position * -0.35f * motionScale;

        for (int i = 0; i < count; i++)
        {
            var ring = rings[i % rings.Count];
            float angle = Hash(i, 1) * MathF.Tau + anim.Time * Easing.Lerp(0.012f, 0.05f, Hash(i, 2)) * motionScale + spin;
            float spread = Easing.Lerp(0.9f, 1.12f, Hash(i, 3));
            var local = new Vector2(MathF.Sin(angle) * ring.Radii.X * spread, MathF.Cos(angle) * ring.Radii.Y * spread);
            local = LayoutMath.Rotate(local, ring.Roll);
            float front = (1f + MathF.Cos(angle)) * 0.5f;
            float twinkle = 0.55f + 0.45f * MathF.Sin(anim.Time * Easing.Lerp(0.6f, 1.6f, Hash(i, 4)) + Hash(i, 5) * 9f);
            float size = Easing.Lerp(1.1f, 2.6f, Hash(i, 6)) * _s * Easing.Lerp(0.7f, 1.15f, front);
            float intensity = p.ParticleAlpha * 1.1f * twinkle * Easing.Lerp(0.3f, 1f, front) * fade;
            if (intensity <= 0.003f) continue;

            ref var c = ref _list.Add(ShaderKind.Glow);
            c.P[0] = new Vector4(ring.Center + local, 1f, 1f);
            c.P[1] = new Vector4(size * 4f, size * 4f, 0f, 0f);
            c.P[4] = new Vector4(ScenePalette.Rgb(tint), intensity);
            c.P[5] = new Vector4(3.2f, 0.35f, 0f, 0f);
        }
    }

    // ───────────────────────────────── cards ─────────────────────────────────

    private void EmitCards()
    {
        var anim = _in.Animator;
        _sorted.Clear();
        float crossfade = anim.Crossfade;
        bool groupFocus = _in.Session.ExpandedGroup is not null;

        foreach (var card in anim.Cards)
        {
            float dim = groupFocus && card.Pose.Focus < 0.5f ? Easing.Lerp(1f, 0.45f, MaxSatellites()) : 1f;
            if (anim.IsCrossfading && card.HasGhost)
            {
                _sorted.Add((card, card.GhostPose, (1f - crossfade) * dim, true));
                _sorted.Add((card, card.Pose, crossfade * dim, false));
            }
            else
            {
                _sorted.Add((card, card.Pose, dim, false));
            }
        }
        foreach (var card in anim.RemovedCards) _sorted.Add((card, card.Pose, 1f, true));
        _sorted.Sort(static (a, b) => a.Pose.SortKey.CompareTo(b.Pose.SortKey));

        // Pass 1: big soft lights behind everything, so glows never wash over neighbouring cards.
        foreach (var (card, pose, alpha, _) in _sorted) EmitCardGlow(card, pose, alpha);

        // Pass 2: the cards themselves, back to front.
        foreach (var (card, pose, alpha, ghost) in _sorted) EmitCard(card, pose, alpha, ghost);

        // Unfolded group satellites sit on top.
        foreach (var card in anim.Cards)
            if (card.Satellites.Value > 0.01f) EmitSatelliteRow(card);
    }

    private float MaxSatellites()
    {
        float v = 0f;
        foreach (var c in _in.Animator.Cards) v = MathF.Max(v, c.Satellites.Value);
        return v;
    }

    private void EmitCardGlow(CardVisual card, in CardPose pose, float alpha)
    {
        var p = _in.Palette;
        float opacity = pose.Opacity * alpha;
        float intensity = p.GlowStrength * (0.12f + 0.5f * pose.Focus) * pose.Glow * opacity * (1f + 0.35f * card.Hover.Value);
        if (intensity <= 0.003f || pose.PreviewSize.X <= 1f) return;

        float spread = Easing.Lerp(1.15f, 1.95f, p.GlowRadius);
        var half = pose.PreviewSize * 0.5f * spread + new Vector2(70f, 80f) * _s * MathF.Max(pose.Scale, 0.35f);
        var project = Project(pose, new Vector2(0f, pose.PreviewSize.Y * 0.1f));

        Vector2 streak = Vector2.Zero;
        if (_in.Animator.Motion.MotionTrails && _in.Quality.MotionTrailsAllowed)
        {
            var v = card.Velocity(_in.Animator.Dt);
            float speed = v.Length();
            if (speed > 40f) streak = v / speed * MathF.Min(0.6f, speed / 3500f);
        }

        ref var c = ref _list.Add(ShaderKind.Glow);
        c.P[0] = new Vector4(project, 1f, 1f);
        c.P[1] = new Vector4(half * (1f + streak.Length() * 0.8f), 0f, 0f);
        c.P[4] = new Vector4(ScenePalette.Rgb(card.Accent), intensity * GlowGain(card.Accent));
        c.P[5] = new Vector4(1.6f, 0.0f, streak.X, streak.Y);
    }

    private void EmitCard(CardVisual card, CardPose pose, float alpha, bool ghost)
    {
        var p = _in.Palette;
        var q = _in.Quality;
        float opacity = pose.Opacity * alpha;
        if (opacity <= 0.004f || pose.PreviewSize.X <= 1f) return;

        bool orbital = LayoutFactory.IsOrbital(_in.Layout.Mode);
        var window = card.Entry.Primary;
        var preview = _in.Resources.GetPreview(window.Handle);
        float k = MathF.Max(pose.Scale, 0.05f);
        float hover = ghost ? 0f : card.Hover.Value;
        float hoverPop = 1f + 0.028f * hover * _in.Animator.Motion.ScaleIntensity;

        // Pointer tilt on hover — a hint of physicality, a few degrees at most.
        float tiltYaw = 0f, tiltPitch = 0f;
        if (hover > 0.01f && _in.Pointer is { } ptr && !_in.Animator.Motion.Reduced)
        {
            var rel = (ptr - pose.Center) / (pose.PreviewSize * 0.5f);
            tiltYaw = Math.Clamp(rel.X, -1f, 1f) * LayoutMath.Deg(3f) * hover;
            tiltPitch = -Math.Clamp(rel.Y, -1f, 1f) * LayoutMath.Deg(3f) * hover;
        }

        float pad = SolarSystemLayout.CardPadding * _s * k;
        float infoFull = orbital ? SolarSystemLayout.InfoStripHeight * _s * k : 0f;
        float infoH = infoFull * pose.InfoAlpha;
        var ph = pose.PreviewSize * 0.5f;
        var outerHalf = new Vector2(ph.X + pad, ph.Y + pad + infoH * 0.5f);
        var outerOffset = new Vector2(0f, infoH * 0.5f);
        float radius = p.CornerRadius * _s * k;
        float previewRadius = MathF.Max(2f * _s, radius - pad * 0.75f);
        float margin = 70f * _s * MathF.Max(k, 0.3f);
        float yaw = pose.Yaw + tiltYaw, pitch = pose.Pitch + tiltPitch;
        uint flags = 0;
        if (window.IsMinimized) flags |= 1;
        if (window.IsHung) flags |= 2;
        if (card.Entry.IsGroup) flags |= 8;

        var accent = card.Accent;
        float frostLod = q.GlassFrost ? _frame.Backdrop.X * 0.85f : -1f;

        // Floor reflection (Cover Flow / Carousel).
        if (pose.Reflection > 0.001f && !ghost)
        {
            float gap = 6f * _s;
            ref var r = ref _list.Add(ShaderKind.Card, TextureRef.Preview(window.Handle));
            FillCard(ref r, pose.Center + new Vector2(0f, (outerHalf.Y + outerOffset.Y) * 2f + gap), hoverPop,
                opacity * pose.Reflection, outerHalf, new Vector2(4f, 4f),
                yaw, pitch, outerOffset, ph, preview, window.AspectRatio, radius, previewRadius, p, accent, pose,
                0f, frostLod, card.PreviewMix.Value, flags | 4);
        }

        if (card.Entry.IsGroup && card.Satellites.Value < 0.99f) EmitSatelliteDots(card, pose, outerHalf, outerOffset, opacity, back: true);

        ref var c = ref _list.Add(ShaderKind.Card, TextureRef.Preview(window.Handle));
        FillCard(ref c, pose.Center, hoverPop, opacity, outerHalf, new Vector2(margin, margin), yaw, pitch,
            outerOffset, ph, preview, window.AspectRatio, radius, previewRadius, p, accent, pose, hover, frostLod, card.PreviewMix.Value, flags);

        if (card.Entry.IsGroup && card.Satellites.Value < 0.99f) EmitSatelliteDots(card, pose, outerHalf, outerOffset, opacity, back: false);

        var t = new AttachedTransform(pose.Center, hoverPop, yaw, pitch, pose.Z);

        // Fallback content (no live preview yet, minimised, other desktop): large icon on an accent gradient.
        float fallback = 1f - card.PreviewMix.Value;
        if (fallback > 0.01f)
        {
            float iconSize = MathF.Min(ph.X, ph.Y) * 0.62f;
            EmitIcon(t, window.App.Id, new Vector2(0f, -iconSize * 0.08f), iconSize, opacity * fallback);
            if (pose.Focus < 0.5f && k > 0.2f && pose.LabelAlpha < 0.99f)
            {
                var name = Text(window.App.DisplayName, 13f * p.FontScale * _s, 500, FontFamilyKind.Text, 360f * _s);
                EmitText(t, name, new Vector2(0f, iconSize * 0.5f + 6f * _s * k), new Vector2(0.5f, 0f),
                    MathF.Min(1f, k / 0.34f), opacity * fallback * 0.8f * (1f - pose.Focus) * (1f - pose.LabelAlpha));
            }
            if (window.IsMinimized && pose.Focus > 0.5f)
            {
                var min = Text("Minimized", 12f * p.FontScale * _s, 500, FontFamilyKind.Text, 200f * _s);
                EmitText(t, min, new Vector2(-ph.X + 12f * _s * k, -ph.Y + 10f * _s * k), Vector2.Zero, k,
                    opacity * fallback * 0.6f * pose.Focus);
            }
        }

        // App icon: a badge on the corner of orbiting cards that glides into the info strip as the card arrives.
        float badge = MathF.Max(20f * _s, 30f * _s * k);
        float stripIcon = 30f * _s * k;
        float iconSizeNow = Easing.Lerp(badge, stripIcon, pose.InfoAlpha);
        var badgePos = new Vector2(-ph.X + badge * 0.42f, ph.Y - badge * 0.05f);
        float stripCenterY = ph.Y + pad + 32f * _s * k - 4f * _s * k;
        var stripPos = new Vector2(-ph.X + 2f * _s * k + stripIcon * 0.5f, stripCenterY);
        float iconAlpha = orbital ? opacity : opacity * (1f - pose.Focus * 0.2f);
        if (fallback < 0.99f || pose.InfoAlpha > 0.01f)
            EmitIcon(t, window.App.Id, Vector2.Lerp(badgePos, stripPos, pose.InfoAlpha), iconSizeNow,
                iconAlpha * MathF.Max(1f - fallback, pose.InfoAlpha));

        // Info strip typography.
        if (orbital && pose.InfoAlpha > 0.01f)
        {
            float textAlpha = opacity * pose.InfoAlpha * pose.InfoAlpha;
            float textLeft = stripPos.X + stripIcon * 0.5f + 12f * _s * k;
            float maxWidth = (pose.PreviewSize.X / k) - (stripIcon / k) - 150f * _s;
            string subtitle = card.Entry.IsGroup && _in.Session.ExpandedGroup != card.Entry
                ? $"{card.Entry.Windows.Count} windows · {window.Subtitle}".TrimEnd(' ', '·')
                : window.Subtitle;
            var name = Text(window.App.DisplayName, 17f * p.FontScale * _s, 600, FontFamilyKind.Display, maxWidth);
            if (subtitle.Length > 0)
            {
                var title = Text(subtitle, 13f * p.FontScale * _s, 400, FontFamilyKind.Text, maxWidth);
                EmitText(t, name, new Vector2(textLeft, stripCenterY - 1f * _s * k), new Vector2(0f, 1f), k, textAlpha);
                EmitText(t, title, new Vector2(textLeft, stripCenterY + 2f * _s * k), new Vector2(0f, 0f), k, textAlpha * 0.6f);
            }
            else
            {
                EmitText(t, name, new Vector2(textLeft, stripCenterY), new Vector2(0f, 0.5f), k, textAlpha);
            }

            float right = ph.X - 4f * _s * k;
            if (window.IsHung)
            {
                var hung = Text("Not responding", 12f * p.FontScale * _s, 500, FontFamilyKind.Text, 220f * _s);
                EmitText(t, hung, new Vector2(right, stripCenterY), new Vector2(1f, 0.5f), k, textAlpha * 0.85f,
                    new Vector4(1f, 0.78f, 0.45f, 1f));
                right -= 120f * _s * k;
            }
            if (window.IsPinned)
            {
                var pin = Text(GlyphPin, 13f * _s, 400, FontFamilyKind.Icons, 40f * _s);
                EmitText(t, pin, new Vector2(right, stripCenterY), new Vector2(1f, 0.5f), k, textAlpha * 0.7f);
            }
        }

        // Group badge (count) on the top-left corner.
        if (card.Entry.IsGroup)
        {
            float bw = 30f * _s * MathF.Max(k, 0.5f), bh = 20f * _s * MathF.Max(k, 0.5f);
            var at = new Vector2(-ph.X + bw * 0.5f + 6f * _s * k, -ph.Y + bh * 0.5f + 6f * _s * k);
            EmitPill(t, at, new Vector2(bw, bh) * 0.5f, bh * 0.5f, opacity * 0.95f, accent, 0.25f);
            var count = Text($"{card.Entry.Windows.Count}", 12f * _s, 600, FontFamilyKind.Text, 60f * _s);
            EmitText(t, count, at, new Vector2(0.5f, 0.5f), MathF.Max(k, 0.5f), opacity * 0.9f);
        }

        // Hover actions: close and pin.
        if (hover > 0.01f && _in.Animator.Phase == OverlayPhase.Open && !ghost)
        {
            float r = 13f * _s;
            var closeAt = new Vector2(outerHalf.X - r - 8f * _s, -outerHalf.Y + outerOffset.Y + r + 8f * _s);
            var pinAt = closeAt - new Vector2(r * 2f + 6f * _s, 0f);
            EmitPill(t, closeAt, new Vector2(r, r), r, opacity * hover, new ColorF(1f, 0.45f, 0.45f), 0.0f);
            EmitText(t, Text(GlyphClose, 10f * _s, 400, FontFamilyKind.Icons, 30f * _s), closeAt, new Vector2(0.5f, 0.5f), 1f, opacity * hover * 0.9f);
            EmitPill(t, pinAt, new Vector2(r, r), r, opacity * hover, accent, window.IsPinned ? 0.5f : 0f);
            EmitText(t, Text(window.IsPinned ? GlyphUnpin : GlyphPin, 11f * _s, 400, FontFamilyKind.Icons, 30f * _s), pinAt,
                new Vector2(0.5f, 0.5f), 1f, opacity * hover * 0.9f);
            AddCircleHit(HitKind.CloseButton, card.Key, t, closeAt, r * 1.1f);
            AddCircleHit(HitKind.PinButton, card.Key, t, pinAt, r * 1.1f);
        }

        // Small name under orbiting cards (expanded stage) or grid cells.
        if (pose.LabelAlpha > 0.01f && !ghost)
        {
            bool grid = _in.Layout.Mode == SwitcherMode.Grid;
            float size = (grid ? 13f : 12f) * p.FontScale * _s;
            var label = Text(window.App.DisplayName, size, grid && pose.Focus > 0.5f ? 600 : 500, FontFamilyKind.Text,
                MathF.Max(120f * _s, pose.PreviewSize.X + (grid ? 0f : 40f * _s)));
            // Cards on the far side of an orbit carry their label above, where it cannot be hidden
            // behind the cards in front of them.
            bool above = orbital && pose.Depth > 0.5f;
            var anchor = above
                ? new Vector2(0f, outerOffset.Y - outerHalf.Y - 7f * _s)
                : new Vector2(0f, outerOffset.Y + outerHalf.Y + 8f * _s);
            EmitText(t, label, anchor, new Vector2(0.5f, above ? 1f : 0f), 1f,
                opacity * pose.LabelAlpha * Easing.Lerp(0.7f, 1f, pose.Focus));
        }

        if (!ghost) AddQuadHit(HitKind.Card, card.Key, -1, t, outerOffset, outerHalf);
    }

    private void FillCard(ref DrawCommand c, Vector2 center, float scale, float opacity, Vector2 half,
        Vector2 margin, float yaw, float pitch, Vector2 offset, Vector2 previewHalf, PreviewInfo preview, float windowAspect,
        float radius, float previewRadius, ScenePalette p, ColorF accent, in CardPose visual, float hover, float frostLod,
        float previewMix, uint flags)
    {
        float k = MathF.Max(visual.Scale, 0.05f);
        c.P[0] = new Vector4(center, scale, opacity);
        c.P[1] = new Vector4(half.X, half.Y, margin.X, margin.Y);
        c.P[2] = new Vector4(yaw, pitch, 0f, visual.Z);
        c.P[3] = new Vector4(offset, 0f, 0f);
        c.P[4] = new Vector4(-previewHalf.X, -previewHalf.Y, previewHalf.X, previewHalf.Y);
        c.P[5] = CoverUv(preview.Available ? preview.Uv : new Vector4(0, 0, 1, 1),
            preview.Available && preview.Aspect > 0 ? preview.Aspect : windowAspect, previewHalf.X / MathF.Max(previewHalf.Y, 1f));
        float border = p.BorderStrength * (0.55f + 0.45f * visual.Focus) + 0.35f * hover;
        c.P[6] = new Vector4(radius, previewRadius, border, p.GlassOpacity);
        c.P[7] = new Vector4(ScenePalette.Rgb(accent), visual.Focus);
        c.P[8] = new Vector4(20f * _s * k, 30f * _s * MathF.Max(k, 0.4f), 0.55f,
            (p.GlowStrength * (0.2f + 0.8f * visual.Focus) + 0.35f * hover) * GlowGain(accent));
        c.P[9] = new Vector4(16f * _s * MathF.Max(k, 0.4f), hover, p.Minimal ? 0.02f : 0.055f, frostLod);
        float dof = _in.Quality.DepthOfField ? visual.Blur * 3.2f : 0f;
        c.P[10] = new Vector4(dof, visual.Brightness, (1f - visual.Brightness) * 0.45f, previewMix);
        c.P[11] = new Vector4(ScenePalette.Rgb(p.GlassTint), flags);
    }

    /// <summary>Crops the texture region so it fills the preview rectangle without distortion ("cover").</summary>
    private static Vector4 CoverUv(Vector4 uv, float textureAspect, float rectAspect)
    {
        if (!(textureAspect > 0) || !(rectAspect > 0)) return uv;
        float w = uv.Z - uv.X, h = uv.W - uv.Y;
        if (textureAspect > rectAspect)
        {
            float keep = rectAspect / textureAspect;
            float cx = uv.X + w * 0.5f;
            return new Vector4(cx - w * keep * 0.5f, uv.Y, cx + w * keep * 0.5f, uv.W);
        }
        else
        {
            float keep = textureAspect / rectAspect;
            // Crop from the bottom: the top of a window (title, tabs) is the most recognisable part.
            return new Vector4(uv.X, uv.Y, uv.Z, uv.Y + h * keep);
        }
    }

    private void EmitSatelliteDots(CardVisual card, in CardPose pose, Vector2 outerHalf, Vector2 outerOffset, float opacity, bool back)
    {
        int m = Math.Min(card.Entry.Windows.Count - 1, 6);
        if (m <= 0) return;
        float k = MathF.Max(pose.Scale, 0.3f);
        float rx = outerHalf.X + 16f * _s * k, ry = outerHalf.Y * 0.28f + 10f * _s * k;
        float spin = _in.Animator.Motion.Reduced ? 0f : _in.Animator.Time * 0.55f;
        float fade = opacity * (1f - card.Satellites.Value);
        var t = new AttachedTransform(pose.Center, 1f, pose.Yaw, pose.Pitch, pose.Z);
        for (int j = 0; j < m; j++)
        {
            float a = spin + j * MathF.Tau / m;
            float front = MathF.Sin(a);
            if (back != (front < 0f)) continue;
            var local = new Vector2(MathF.Cos(a) * rx, outerOffset.Y + front * ry);
            float size = 3.2f * _s * k * Easing.Lerp(0.75f, 1.15f, (front + 1f) * 0.5f);
            ref var c = ref _list.Add(ShaderKind.Glow);
            SetAttached(ref c, t, local, new Vector2(size * 3.5f));
            c.P[4] = new Vector4(ScenePalette.Rgb(card.Accent), fade * Easing.Lerp(0.35f, 1f, (front + 1f) * 0.5f) * 1.2f);
            c.P[5] = new Vector4(2.6f, 0.45f, 0f, 0f);
        }
    }

    private void EmitSatelliteRow(CardVisual card)
    {
        var p = _in.Palette;
        var windows = card.Entry.Windows;
        int m = windows.Count;
        float unfold = card.Satellites.Value;
        float opacity = unfold * card.Pose.Opacity;
        if (opacity <= 0.01f) return;

        float boxW = 150f, boxH = 94f, spacing = 18f;
        float total = m * boxW + (m - 1) * spacing;
        float fit = MathF.Min(1f, 1240f / total);
        boxW *= fit; boxH *= fit; spacing *= fit;
        total = m * boxW + (m - 1) * spacing;
        float y = _in.Animator.Layout.FocusBottom + (22f + boxH * 0.5f) * _s;
        float x0 = card.Pose.Center.X - total * 0.5f * _s + boxW * 0.5f * _s;
        float k = boxW / SolarSystemLayout.CenterBoxWidth;

        for (int j = 0; j < m; j++)
        {
            var w = windows[j];
            bool selected = j == card.Entry.SubIndex;
            var size = LayoutMath.Fit(w.AspectRatio, boxW, boxH) * _s;
            var target = new Vector2(x0 + j * (boxW + spacing) * _s, y);
            var center = Vector2.Lerp(card.Pose.Center, target, Easing.Smootherstep(unfold));
            var pose = new CardPose
            {
                Center = center,
                PreviewSize = size * Easing.Lerp(0.6f, 1f, unfold) * (selected ? 1.06f : 1f),
                Scale = k * (selected ? 1.06f : 1f),
                Opacity = opacity,
                Focus = selected ? 1f : 0.25f,
                Brightness = selected ? 1f : 0.78f,
                Glow = selected ? 1f : 0.3f,
            };
            var preview = _in.Resources.GetPreview(w.Handle);
            var ph = pose.PreviewSize * 0.5f;
            float pad = 6f * _s;
            float radius = 12f * _s;
            ref var c = ref _list.Add(ShaderKind.Card, TextureRef.Preview(w.Handle));
            FillCard(ref c, pose.Center, 1f, opacity, ph + new Vector2(pad), new Vector2(40f * _s), 0f, 0f,
                Vector2.Zero, ph, preview, w.AspectRatio, radius, radius - pad * 0.6f, p, card.Accent, pose, selected ? 0.6f : 0f,
                -1f, preview.Available ? 1f : 0f, 0u);

            var t = new AttachedTransform(pose.Center, 1f, 0f, 0f, 0f);
            if (!preview.Available) EmitIcon(t, w.App.Id, Vector2.Zero, MathF.Min(ph.X, ph.Y) * 0.8f, opacity);
            var label = Text(string.IsNullOrWhiteSpace(w.Subtitle) ? w.App.DisplayName : w.Subtitle, 11.5f * p.FontScale * _s,
                selected ? 600 : 400, FontFamilyKind.Text, boxW * _s + 10f * _s);
            EmitText(t, label, new Vector2(0f, ph.Y + pad + 6f * _s), new Vector2(0.5f, 0f), 1f, opacity * (selected ? 0.95f : 0.6f));
            AddQuadHit(HitKind.Satellite, card.Key, j, t, Vector2.Zero, ph + new Vector2(pad));
        }
    }

    // ───────────────────────────────── chrome ─────────────────────────────────

    private void EmitChrome()
    {
        var anim = _in.Animator;
        var session = _in.Session;
        var p = _in.Palette;
        var vp = _in.Layout.Space.Viewport;
        float stage = anim.Stage.Value;
        float chrome = _reveal * _live;
        var screen = new AttachedTransform(Vector2.Zero, 1f, 0f, 0f, 0f);
        var accent = OkLab.ToSrgb(anim.AmbientLab.Value);

        // Search pill.
        // The search field only exists while typing; until then a quiet hint lives in the key legend.
        float search = anim.SearchPresence.Value * chrome;
        if (search > 0.01f && session.Options.SearchEnabled)
        {
            float w = 440f * _s, h = 40f * _s;
            var at = new Vector2(vp.X * 0.5f, (38f + 10f * (1f - search)) * _s + h * 0.5f);
            EmitPill(screen, at, new Vector2(w, h) * 0.5f, h * 0.5f, search, accent, anim.SearchPresence.Value * 0.6f);
            EmitText(screen, Text(GlyphSearch, 14f * _s, 400, FontFamilyKind.Icons, 40f * _s),
                at + new Vector2(-w * 0.5f + 20f * _s, 0f), new Vector2(0.5f, 0.5f), 1f, search * 0.7f);
            bool hasQuery = session.Query.Length > 0;
            var text = Text(hasQuery ? session.Query : "Type to search", 15f * p.FontScale * _s, hasQuery ? 500 : 400,
                FontFamilyKind.Text, w - 60f * _s);
            EmitText(screen, text, at + new Vector2(-w * 0.5f + 40f * _s, 0f), new Vector2(0f, 0.5f), 1f,
                search * (hasQuery ? 0.95f : 0.42f));
        }

        if (anim.NoResults.Value > 0.01f)
        {
            var msg = Text($"No windows match “{session.Query}”", 17f * p.FontScale * _s, 500, FontFamilyKind.Display, 900f * _s);
            EmitText(screen, msg, anim.Layout.Anchor, new Vector2(0.5f, 0.5f), 1f, anim.NoResults.Value * chrome * 0.7f);
        }

        // Caption for non-orbital layouts (the selected window's name under the focus area).
        if (!LayoutFactory.IsOrbital(_in.Layout.Mode) && anim.FocusedCard is { } focus && session.Entries.Count > 0)
        {
            float a = focus.Pose.Focus * chrome * focus.Pose.Opacity;
            if (a > 0.01f)
            {
                var w = focus.Entry.Primary;
                float y = anim.Layout.FocusBottom + 26f * _s;
                var name = Text(w.App.DisplayName, 20f * p.FontScale * _s, 600, FontFamilyKind.Display, 900f * _s);
                EmitText(screen, name, new Vector2(vp.X * 0.5f, y), new Vector2(0.5f, 0f), 1f, a);
                if (w.Subtitle.Length > 0)
                    EmitText(screen, Text(w.Subtitle, 14f * p.FontScale * _s, 400, FontFamilyKind.Text, 900f * _s),
                        new Vector2(vp.X * 0.5f, y + 30f * _s * p.FontScale), new Vector2(0.5f, 0f), 1f, a * 0.6f);
            }
        }

        // Virtual desktops.
        var desktops = _in.Desktops;
        float deskAlpha = stage * chrome;
        if (desktops.Count > 1 && deskAlpha > 0.01f)
        {
            float chipW = 136f * _s, chipH = 34f * _s, gap = 10f * _s;
            float total = desktops.Count * chipW + (desktops.Count - 1) * gap;
            float y = vp.Y - 44f * _s;
            float x = vp.X * 0.5f - total * 0.5f + chipW * 0.5f;
            for (int i = 0; i < desktops.Count; i++)
            {
                var d = desktops[i];
                var at = new Vector2(x + i * (chipW + gap), y + (1f - stage) * 16f * _s);
                EmitPill(screen, at, new Vector2(chipW, chipH) * 0.5f, chipH * 0.5f, deskAlpha, accent, d.IsCurrent ? 0.7f : 0f);
                EmitText(screen, Text(GlyphDesktop, 12f * _s, 400, FontFamilyKind.Icons, 30f * _s),
                    at + new Vector2(-chipW * 0.5f + 18f * _s, 0f), new Vector2(0.5f, 0.5f), 1f, deskAlpha * (d.IsCurrent ? 0.9f : 0.5f));
                EmitText(screen, Text(d.Name, 12.5f * p.FontScale * _s, d.IsCurrent ? 600 : 400, FontFamilyKind.Text, chipW - 50f * _s),
                    at + new Vector2(-chipW * 0.5f + 32f * _s, 0f), new Vector2(0f, 0.5f), 1f, deskAlpha * (d.IsCurrent ? 0.95f : 0.6f));
                if (d.WindowCount > 0)
                    EmitText(screen, Text(d.WindowCount.ToString(), 11f * _s, 500, FontFamilyKind.Text, 40f * _s),
                        at + new Vector2(chipW * 0.5f - 14f * _s, 0f), new Vector2(1f, 0.5f), 1f, deskAlpha * 0.45f);
                AddCircleHit(HitKind.Desktop, null, screen, at, chipH * 0.5f, i, new Vector2(chipW, chipH) * 0.5f);
            }
        }

        // Keyboard hints.
        if (_in.ShowHints && deskAlpha > 0.01f && session.Mode != SessionMode.SameApp)
        {
            string searchHint = session.Options.SearchEnabled && !session.IsSearching ? "Type to search     " : string.Empty;
            string hint = searchHint + (session.Mode == SessionMode.Sticky
                ? "Enter  open     Del  close     Esc  cancel"
                : "Release Alt  open     Del  close     Esc  cancel");
            var t = Text(hint, 12f * p.FontScale * _s, 400, FontFamilyKind.Text, 700f * _s);
            EmitText(screen, t, new Vector2(vp.X - 32f * _s, vp.Y - 36f * _s), new Vector2(1f, 0.5f), 1f, deskAlpha * 0.32f);
        }

        if (_in.Stats is { Length: > 0 } stats)
            EmitText(screen, Text(stats, 12f * _s, 400, FontFamilyKind.Text, 900f * _s), new Vector2(16f * _s, 14f * _s),
                Vector2.Zero, 1f, 0.55f * chrome);
    }

    // ───────────────────────────────── primitives ─────────────────────────────────

    private readonly record struct AttachedTransform(Vector2 Center, float Scale, float Yaw, float Pitch, float Z);

    private static void SetAttached(ref DrawCommand c, in AttachedTransform t, Vector2 localCenter, Vector2 half, float opacity = 1f)
    {
        c.P[0] = new Vector4(t.Center, t.Scale, opacity);
        c.P[1] = new Vector4(half, 0f, 0f);
        c.P[2] = new Vector4(t.Yaw, t.Pitch, 0f, t.Z);
        c.P[3] = new Vector4(localCenter, 0f, 0f);
    }

    private void EmitIcon(in AttachedTransform t, string appId, Vector2 localCenter, float size, float alpha)
    {
        if (alpha <= 0.004f || size < 2f) return;
        ref var c = ref _list.Add(ShaderKind.Sprite, TextureRef.Icon(appId));
        var half = new Vector2(size * 0.5f);
        SetAttached(ref c, t, localCenter, half);
        c.P[4] = new Vector4(localCenter - half, localCenter.X + half.X, localCenter.Y + half.Y);
        c.P[5] = new Vector4(0f, 0f, 1f, 1f);
        c.P[6] = new Vector4(alpha, alpha, alpha, alpha);
        c.P[7] = new Vector4(-0.25f, 0f, 0f, 1f);
    }

    private void EmitText(in AttachedTransform t, TextSpec spec, Vector2 anchor, Vector2 align, float rasterToLocal,
        float alpha, Vector4? color = null)
    {
        if (alpha <= 0.004f || spec.Text.Length == 0) return;
        ref var c = ref _list.Add(ShaderKind.Sprite, TextureRef.ForText(spec));
        SetAttached(ref c, t, anchor, Vector2.One);
        var col = color ?? Vector4.One;
        c.P[4] = new Vector4(anchor, align.X, align.Y);
        c.P[5] = new Vector4(0f, 0f, 1f, 1f);
        c.P[6] = new Vector4(col.X * alpha, col.Y * alpha, col.Z * alpha, alpha);
        c.P[7] = new Vector4(-0.2f, 0f, TextPlacement.TextFlag, rasterToLocal);
    }

    private void EmitPill(in AttachedTransform t, Vector2 localCenter, Vector2 half, float radius, float alpha, ColorF accent, float accentMix)
    {
        if (alpha <= 0.004f) return;
        var p = _in.Palette;
        ref var c = ref _list.Add(ShaderKind.Pill);
        c.P[0] = new Vector4(t.Center, t.Scale, alpha);
        c.P[1] = new Vector4(half, 26f * _s, 26f * _s);
        c.P[2] = new Vector4(t.Yaw, t.Pitch, 0f, t.Z);
        c.P[3] = new Vector4(localCenter, 0f, 0f);
        c.P[4] = new Vector4(radius, p.BorderStrength, MathF.Min(1f, p.GlassOpacity + 0.08f), _in.Quality.GlassFrost ? _frame.Backdrop.X * 0.8f : -1f);
        c.P[5] = new Vector4(ScenePalette.Rgb(p.GlassTint), accentMix);
        c.P[6] = new Vector4(ScenePalette.Rgb(accent), accentMix);
        c.P[7] = new Vector4(localCenter, 0f, 0f);
    }

    private TextSpec Text(string text, float sizePx, int weight, FontFamilyKind family, float maxWidth)
    {
        var key = (text, MathF.Round(sizePx * 2f) / 2f, weight, family, MathF.Round(maxWidth / 8f) * 8f);
        if (_textCache.TryGetValue(key, out var spec)) return spec;
        if (_textCache.Count > 1024) _textCache.Clear();
        spec = TextSpec.Create(text, sizePx, weight, family, maxWidth);
        _textCache[key] = spec;
        return spec;
    }

    private Vector2 Project(in CardPose pose, Vector2 local) =>
        Projection.Project(local, new Vector4(pose.Center, 1f, 1f), new Vector4(pose.Yaw, pose.Pitch, 0f, pose.Z), _frame);

    // ───────────────────────────────── hit testing ─────────────────────────────────

    private void AddQuadHit(HitKind kind, string key, int sub, in AttachedTransform t, Vector2 offset, Vector2 half)
    {
        var p0 = new Vector4(t.Center, t.Scale, 1f);
        var p2 = new Vector4(t.Yaw, t.Pitch, 0f, t.Z);
        _hits.Add(new HitRegion(kind, key, sub, -1, HitShape.Quad,
            Projection.Project(offset + new Vector2(-half.X, -half.Y), p0, p2, _frame),
            Projection.Project(offset + new Vector2(half.X, -half.Y), p0, p2, _frame),
            Projection.Project(offset + new Vector2(half.X, half.Y), p0, p2, _frame),
            Projection.Project(offset + new Vector2(-half.X, half.Y), p0, p2, _frame), 0f));
    }

    private void AddCircleHit(HitKind kind, string? key, in AttachedTransform t, Vector2 local, float radius, int desktop = -1, Vector2? quadHalf = null)
    {
        var p0 = new Vector4(t.Center, t.Scale, 1f);
        var p2 = new Vector4(t.Yaw, t.Pitch, 0f, t.Z);
        if (quadHalf is { } h)
        {
            _hits.Add(new HitRegion(kind, key, -1, desktop, HitShape.Quad,
                Projection.Project(local + new Vector2(-h.X, -h.Y), p0, p2, _frame),
                Projection.Project(local + new Vector2(h.X, -h.Y), p0, p2, _frame),
                Projection.Project(local + new Vector2(h.X, h.Y), p0, p2, _frame),
                Projection.Project(local + new Vector2(-h.X, h.Y), p0, p2, _frame), 0f));
            return;
        }
        var c = Projection.Project(local, p0, p2, _frame);
        _hits.Add(new HitRegion(kind, key, -1, desktop, HitShape.Circle, c, c, c, c, radius));
    }

    /// <summary>Finds the topmost interactive element under <paramref name="px"/> (overlay pixels).</summary>
    public HitResult HitTest(Vector2 px)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            bool inside = h.Shape == HitShape.Circle
                ? Vector2.DistanceSquared(px, h.A) <= h.Radius * h.Radius
                : Projection.PointInQuad(px, h.A, h.B, h.C, h.D);
            if (inside) return new HitResult(h.Kind, h.Key, h.Sub, h.Desktop);
        }
        return new HitResult(HitKind.Background);
    }

    /// <summary>
    /// Perceptual gain for coloured light: yellow and green read far brighter than blue or violet
    /// at the same intensity, so bright hues are toned down and deep hues lifted slightly.
    /// </summary>
    private static float GlowGain(ColorF c)
    {
        float l = MathF.Max(0.05f, c.Luminance);
        return Math.Clamp(MathF.Pow(0.3f / l, 0.9f), 0.45f, 1.25f);
    }

    private static float Hash(int i, int salt)
    {
        uint x = (uint)(i * 747796405 + salt * 2891336453);
        x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
        x = (x >> 22) ^ x;
        return (x & 0xFFFFFF) / 16777216f;
    }
}
