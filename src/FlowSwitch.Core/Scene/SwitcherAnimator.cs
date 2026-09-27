using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Model;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Scene;

public enum OverlayPhase
{
    Open,
    Committing,
    Cancelling,
    Done,
}

/// <summary>Animated state of one card, carried across layout changes by entry key.</summary>
public sealed class CardVisual
{
    public CardVisual(SwitcherEntry entry) { Entry = entry; Key = entry.Key; }

    public string Key { get; }
    public SwitcherEntry Entry { get; set; }
    public int Index;
    public bool Removed;
    public CardPose Pose;
    public CardPose PreviousPose;
    public CardPose FlipFrom;
    public Spring Flip;
    public Spring Presence;
    public Spring Hover;
    public Spring Satellites;
    public Spring PreviewMix;
    public Spring3 AccentLab;
    public float RevealDelay;
    public CardPose GhostPose;
    public bool HasGhost;

    public ColorF Accent => OkLab.ToSrgb(AccentLab.Value);

    public Vector2 Velocity(float dt) => dt > 0 ? (Pose.Center - PreviousPose.Center) / dt : Vector2.Zero;
}

/// <summary>
/// Owns every time-varying visual quantity of the overlay: the orbital rotor, reveal/exit
/// progress, per-card springs (presence, hover, FLIP layout transitions), ambient light colour
/// and parallax. It observes the session; it never changes it.
/// </summary>
public sealed class SwitcherAnimator
{
    private readonly Dictionary<string, CardVisual> _cards = new(StringComparer.Ordinal);
    private readonly List<CardVisual> _ordered = new();
    private readonly List<CardVisual> _removed = new();
    private readonly LayoutResult _layout = new();
    private LayoutItem[] _items = Array.Empty<LayoutItem>();
    private string[] _keys = Array.Empty<string>();
    private int _layoutVersion = -1;
    private bool _wraps = true;
    private long _lastSessionTarget;
    private Tween _revealBackdrop;
    private Tween _exit;
    private Tween _crossfade;
    private bool _crossfading;
    private Vector4? _exitTarget;
    private string? _exitKey;
    private float _exitStartScale = 1f;

    public OrbitalRotor Rotor { get; } = new();
    public OverlayPhase Phase { get; private set; } = OverlayPhase.Open;
    public float Time { get; private set; }
    public float Dt { get; private set; }
    public MotionProfile Motion { get; private set; } = MotionProfile.Smooth;
    public LayoutResult Layout => _layout;

    /// <summary>Visible cards in entry order.</summary>
    public IReadOnlyList<CardVisual> Cards => _ordered;

    /// <summary>Cards whose windows disappeared, still fading out.</summary>
    public IReadOnlyList<CardVisual> RemovedCards => _removed;

    public Spring Stage;
    public Spring SearchPresence;
    public Spring NoResults;
    public Spring3 AmbientLab;
    public Spring3 AmbientPrevLab;
    public Spring3 AmbientNextLab;
    public Spring2 Parallax;

    /// <summary>0..1 eased reveal of the backdrop (blur, dim).</summary>
    public float BackdropReveal { get; private set; }

    /// <summary>0..1 eased exit progress (commit or cancel).</summary>
    public float ExitProgress { get; private set; }

    /// <summary>Linear exit progress.</summary>
    public float ExitLinear { get; private set; }

    /// <summary>0..1 cross-fade progress when reduced motion is active.</summary>
    public float Crossfade => _crossfading ? _crossfade.Value : 1f;

    public bool IsCrossfading => _crossfading;

    public string? HoveredKey { get; set; }

    public bool IsFinished => Phase == OverlayPhase.Done;

    public bool IsAnimating =>
        Phase != OverlayPhase.Open || !Rotor.IsSettled || _crossfading || BackdropReveal < 1f ||
        _removed.Count > 0 || MotionHasIdle;

    private bool MotionHasIdle => Motion.IdleAmount > 0 || Motion.BreathingAmplitude > 0;

    public void Begin(SwitcherSession session, MotionProfile motion)
    {
        Motion = motion;
        Phase = OverlayPhase.Open;
        Time = 0f;
        _cards.Clear();
        _ordered.Clear();
        _removed.Clear();
        _layoutVersion = -1;
        _crossfading = false;
        _exitTarget = null;
        _exitKey = null;
        Rotor.Reset(session.SelectionTarget);
        Rotor.MaxLag = motion.Reduced ? 0.01 : 2.6;
        _lastSessionTarget = session.SelectionTarget;
        _revealBackdrop = new Tween(motion.RevealBlurDuration, Easing.Soft);
        Stage = new Spring(session.Stage == SessionStage.Expanded ? 1f : 0f);
        SearchPresence = new Spring(0f);
        NoResults = new Spring(0f);
        Parallax = new Spring2(Vector2.Zero);
        BackdropReveal = 0f;
        ExitProgress = 0f;
        ExitLinear = 0f;
        HoveredKey = null;

        var selected = session.Selected;
        var accent = OkLab.FromSrgb(selected?.Primary.Accent ?? ColorF.NeutralAccent);
        AmbientLab = new Spring3(accent);
        AmbientPrevLab = new Spring3(accent);
        AmbientNextLab = new Spring3(accent);
        SyncEntries(session, initial: true);
    }

    /// <summary>Starts the closing animation. <paramref name="targetRect"/> is the activated window's frame in overlay pixels.</summary>
    public void BeginExit(bool committed, string? targetKey, Vector4? targetRect)
    {
        if (Phase is OverlayPhase.Committing or OverlayPhase.Cancelling or OverlayPhase.Done) return;
        Phase = committed ? OverlayPhase.Committing : OverlayPhase.Cancelling;
        _exit = new Tween(committed ? Motion.ExitDuration : Motion.CancelDuration, committed ? Easing.Standard : Easing.Exit);
        _exitTarget = committed && !Motion.Reduced ? targetRect : null;
        _exitKey = committed ? targetKey ?? FocusedCard?.Key : null;
        _exitStartScale = _exitKey is not null && _cards.TryGetValue(_exitKey, out var card) ? card.Pose.Scale : 1f;
    }

    /// <summary>Skips straight to the end (quick switch without a visible overlay).</summary>
    public void Finish() => Phase = OverlayPhase.Done;

    public CardVisual? FocusedCard
    {
        get
        {
            CardVisual? best = null;
            foreach (var c in _ordered)
                if (best is null || c.Pose.Focus > best.Pose.Focus) best = c;
            return best;
        }
    }

    public void Update(float dt, SwitcherSession session, ILayoutEngine engine, LayoutContext ctx,
                       ISceneResources resources, ScenePalette palette, Vector2? pointerNormalized)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        Dt = dt;
        Time += dt;
        Motion = ctx.Motion;

        SyncEntries(session, initial: false);
        _wraps = engine.Wraps;
        Rotor.MaxLag = Motion.Reduced ? 0.01 : _wraps ? 2.6 : Math.Max(2.6, session.Entries.Count);
        StepSelection(dt, session);

        Stage.Step(session.Stage == SessionStage.Expanded ? 1f : 0f, dt, Motion.Stage);
        SearchPresence.Step(session.IsSearching ? 1f : 0f, dt, Motion.Micro);
        NoResults.Step(session.IsSearching && session.Entries.Count == 0 ? 1f : 0f, dt, Motion.Micro);
        Parallax.Target = pointerNormalized ?? Vector2.Zero;
        Parallax.Step(dt, Motion.Parallax);

        _revealBackdrop.Step(dt);
        BackdropReveal = _revealBackdrop.Value;

        if (Phase is OverlayPhase.Committing or OverlayPhase.Cancelling)
        {
            _exit.Step(dt);
            ExitProgress = _exit.Value;
            ExitLinear = _exit.Linear;
            if (_exit.IsComplete) Phase = OverlayPhase.Done;
        }

        // ── Layout ──────────────────────────────────────────────────────────────
        ctx.RotorPosition = Rotor.Position;
        ctx.RotorVelocity = Motion.Reduced ? 0 : Rotor.Velocity;
        ctx.Expansion = Stage.Value;
        ctx.Time = Time;
        ctx.Parallax = Parallax.Value;
        engine.Compute(ctx, _items, _layout);

        float revealTime = Time;
        for (int i = 0; i < _ordered.Count; i++)
        {
            var card = _ordered[i];
            card.PreviousPose = card.Pose;
            CardPose pose = _layout.Poses[i];

            card.Flip.Step(dt, Motion.Layout);
            if (card.Flip.Value > 0.0005f) pose = CardPose.Lerp(pose, card.FlipFrom, card.Flip.Value);

            card.Presence.Step(dt, Motion.Presence);
            ApplyPresence(ref pose, card.Presence.Value);
            ApplyReveal(ref pose, card, revealTime);

            card.Hover.Step(HoveredKey == card.Key ? 1f : 0f, dt, Motion.Micro);
            bool expanded = session.ExpandedGroup is not null && session.ExpandedGroup.Key == card.Key;
            card.Satellites.Step(expanded ? 1f : 0f, dt, Motion.Layout);
            card.PreviewMix.Step(resources.GetPreview(card.Entry.Primary.Handle).Available ? 1f : 0f, dt, Motion.Presence);
            card.AccentLab.Target = OkLab.FromSrgb(card.Entry.Primary.Accent);
            card.AccentLab.Step(dt, Motion.Color);

            ApplyExit(ref pose, card);
            card.Pose = pose;
        }

        for (int i = _removed.Count - 1; i >= 0; i--)
        {
            var card = _removed[i];
            card.Presence.Step(0f, dt, Motion.Presence);
            var pose = card.Pose;
            pose.Opacity = card.Presence.Value * card.PreviousPose.Opacity;
            pose.Scale = card.PreviousPose.Scale * Easing.Lerp(0.82f, 1f, card.Presence.Value);
            pose.PreviewSize = card.PreviousPose.PreviewSize * Easing.Lerp(0.82f, 1f, card.Presence.Value);
            card.Pose = pose;
            if (card.Presence.Value < 0.01f) _removed.RemoveAt(i);
        }

        if (_crossfading)
        {
            _crossfade.Step(dt);
            if (_crossfade.IsComplete)
            {
                _crossfading = false;
                foreach (var c in _ordered) c.HasGhost = false;
            }
        }

        UpdateAmbient(session, palette, dt);
    }

    private void StepSelection(float dt, SwitcherSession session)
    {
        long delta = session.SelectionTarget - _lastSessionTarget;
        _lastSessionTarget = session.SelectionTarget;
        if (delta != 0)
        {
            // Rings keep turning the same way across the wrap; rows travel directly to the index.
            if (_wraps) Rotor.Advance((int)delta);
            else Rotor.SetTarget(Math.Max(0, session.SelectedIndex));

            if (Motion.Reduced)
            {
                // Reduced motion: no travel. Snapshot the current poses and cross-fade in place.
                foreach (var c in _ordered) { c.GhostPose = c.Pose; c.HasGhost = true; }
                Rotor.Reset(Rotor.Target);
                _crossfade = new Tween(Motion.CrossfadeDuration, Easing.Standard);
                _crossfading = true;
            }
        }
        Rotor.Step(dt, Motion.Rotor);
    }

    private void SyncEntries(SwitcherSession session, bool initial)
    {
        if (!initial && session.LayoutVersion == _layoutVersion) return;
        _layoutVersion = session.LayoutVersion;

        var entries = session.Entries;
        bool sameKeys = entries.Count == _keys.Length;
        if (sameKeys)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Key != _keys[i]) { sameKeys = false; break; }
        }

        if (_items.Length != entries.Count) _items = new LayoutItem[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            _items[i] = new LayoutItem(entries[i].Primary.AspectRatio, entries[i].IsGroup, entries[i].Windows.Count);

        if (sameKeys && !initial)
        {
            for (int i = 0; i < entries.Count; i++) _ordered[i].Entry = entries[i];
            if (_wraps) Rotor.SeekTo(session.SelectedIndex, entries.Count);
            else Rotor.SetTarget(Math.Max(0, session.SelectedIndex));
            _lastSessionTarget = session.SelectionTarget;
            return;
        }

        // FLIP: remember where everything is, then animate from there to the new layout.
        var alive = new HashSet<string>(StringComparer.Ordinal);
        _ordered.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            alive.Add(entry.Key);
            if (_cards.TryGetValue(entry.Key, out var card))
            {
                card.Entry = entry;
                if (!initial)
                {
                    card.FlipFrom = card.Pose;
                    card.Flip = new Spring(1f) { Target = 0f };
                }
            }
            else
            {
                card = new CardVisual(entry)
                {
                    Presence = new Spring(initial ? 1f : 0f) { Target = 1f },
                    AccentLab = new Spring3(OkLab.FromSrgb(entry.Primary.Accent)),
                    PreviewMix = new Spring(0f),
                };
                _cards[entry.Key] = card;
            }
            card.Index = i;
            card.Removed = false;
            _ordered.Add(card);
        }

        foreach (var (key, card) in _cards.ToList())
        {
            if (alive.Contains(key)) continue;
            _cards.Remove(key);
            if (!initial)
            {
                card.Removed = true;
                card.PreviousPose = card.Pose;
                _removed.Add(card);
            }
        }

        _keys = entries.Select(e => e.Key).ToArray();
        Rotor.Reset(session.SelectedIndex < 0 ? 0 : session.SelectedIndex);
        _lastSessionTarget = session.SelectionTarget;

        if (initial)
        {
            // Stagger the entrance by distance from the selected card.
            int n = _ordered.Count;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(OrbitalRotor.WrapOffset(i - Rotor.Position, n));
                _ordered[i].RevealDelay = Motion.RevealStagger * (float)Math.Min(d, 6);
            }
        }
    }

    private static void ApplyPresence(ref CardPose pose, float presence)
    {
        if (presence >= 0.999f) return;
        float k = Easing.Lerp(0.86f, 1f, presence);
        pose.Opacity *= presence;
        pose.Scale *= k;
        pose.PreviewSize *= k;
    }

    private void ApplyReveal(ref CardPose pose, CardVisual card, float time)
    {
        float r = Math.Clamp((time - card.RevealDelay) / Motion.RevealDuration, 0f, 1f);
        if (r >= 1f) return;
        float e = Easing.Enter.Evaluate(r);
        float lift = Motion.Reduced ? 1f : Easing.Lerp(0.9f, 1f, e);
        pose.Center = Vector2.Lerp(_layout.Anchor, pose.Center, Motion.Reduced ? 1f : Easing.Lerp(0.86f, 1f, e));
        pose.Scale *= lift;
        pose.PreviewSize *= lift;
        pose.Opacity *= Easing.Smoothstep(0f, 0.8f, r);
        pose.Glow *= e;
    }

    private void ApplyExit(ref CardPose pose, CardVisual card)
    {
        if (Phase is not (OverlayPhase.Committing or OverlayPhase.Cancelling or OverlayPhase.Done)) return;
        float e = ExitProgress;
        float lin = ExitLinear;
        if (Phase is OverlayPhase.Committing or OverlayPhase.Done && _exitKey == card.Key)
        {
            if (_exitTarget is { } rect)
            {
                // The card flies into the real window's frame and dissolves into it.
                var targetCenter = new Vector2(rect.X + rect.Z * 0.5f, rect.Y + rect.W * 0.5f);
                var targetSize = new Vector2(rect.Z, rect.W);
                float growth = pose.PreviewSize.X > 0 ? targetSize.X / pose.PreviewSize.X : 1f;
                pose.Center = Vector2.Lerp(pose.Center, targetCenter, e);
                pose.PreviewSize = Vector2.Lerp(pose.PreviewSize, targetSize, e);
                pose.Scale = Easing.Lerp(pose.Scale, _exitStartScale * growth, e);
                pose.Yaw *= 1f - e;
            }
            else
            {
                float k = Easing.Lerp(1f, 1.05f, e);
                pose.Scale *= k;
                pose.PreviewSize *= k;
            }
            pose.Opacity *= 1f - Easing.Smoothstep(0.45f, 1f, lin);
            pose.InfoAlpha *= 1f - Easing.Smoothstep(0f, 0.4f, lin);
            pose.Glow *= 1f - e;
            return;
        }

        float shrink = Motion.Reduced ? 1f : Easing.Lerp(1f, 0.94f, e);
        pose.Center = Vector2.Lerp(pose.Center, _layout.Anchor, Motion.Reduced ? 0f : e * 0.06f);
        pose.Scale *= shrink;
        pose.PreviewSize *= shrink;
        pose.Opacity *= 1f - Easing.Smoothstep(0f, 0.75f, lin);
        pose.Glow *= 1f - e;
    }

    private void UpdateAmbient(SwitcherSession session, ScenePalette palette, float dt)
    {
        ColorF main, prev, next;
        var cards = _ordered;
        if (palette.AmbientSource == AmbientColorSource.Custom)
        {
            main = prev = next = palette.AmbientCustom;
        }
        else if (palette.AmbientSource == AmbientColorSource.Neutral || cards.Count == 0)
        {
            main = prev = next = ColorF.NeutralAccent;
        }
        else
        {
            int n = cards.Count;
            int sel = Math.Clamp(session.SelectedIndex, 0, n - 1);
            main = cards[sel].Entry.Primary.Accent;
            prev = cards[(sel - 1 + n) % n].Entry.Primary.Accent;
            next = cards[(sel + 1) % n].Entry.Primary.Accent;
        }

        AmbientLab.Target = OkLab.FromSrgb(main);
        AmbientPrevLab.Target = OkLab.FromSrgb(prev);
        AmbientNextLab.Target = OkLab.FromSrgb(next);
        AmbientLab.Step(dt, Motion.Color);
        AmbientPrevLab.Step(dt, Motion.Color.Scaled(1.4f));
        AmbientNextLab.Step(dt, Motion.Color.Scaled(1.4f));
    }
}
