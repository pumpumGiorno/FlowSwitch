using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Scene;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

/// <summary>Appearance → Window size: the selected and orbit card sizes in every layout.</summary>
public class CardSizeTests
{
    private static readonly Vector2[] Screens =
    {
        new(1920, 1080), new(2560, 1440), new(3440, 1440), new(3840, 2160), new(1366, 768), new(2560, 1600), new(5120, 1440),
    };

    private const float MinSelected = AppearanceSettings.MinSelectedCardSize, MaxSelected = AppearanceSettings.MaxSelectedCardSize;
    private const float MinOrbit = AppearanceSettings.MinOrbitCardSize, MaxOrbit = AppearanceSettings.MaxOrbitCardSize;

    /// <summary>Minimum, default and maximum sizes, the presets, and the lopsided extremes.</summary>
    public static TheoryData<float, float> Sizes => new()
    {
        { MinSelected, MinOrbit },
        { 1f, 1f },
        { MaxSelected, MaxOrbit },
        { 0.85f, 0.7f },
        { 1.25f, 0.9f },
        { 1.5f, 1.05f },
        { MaxSelected, MinOrbit },
        { MinSelected, MaxOrbit },
    };

    private static LayoutContext Context(Vector2 screen, CardSizing size, SwitcherMode mode = SwitcherMode.SolarSystem,
        double rotor = 0, float expansion = 0f) => new()
    {
        Space = DesignSpace.For(screen),
        RotorPosition = rotor,
        Expansion = expansion,
        Motion = MotionProfile.Smooth with { IdleAmount = 0f, BreathingAmplitude = 0f },
        Solar = new SolarSystemSettings(),
        Mode = mode,
        TargetCardSize = size,
        CardSize = size,
    };

    private static LayoutItem[] Items(int n) => Enumerable.Range(0, n).Select(i => new LayoutItem(i % 3 == 0 ? 1.78f : 1.6f, false, 1)).ToArray();

    private readonly record struct Rect(Vector2 Min, Vector2 Max)
    {
        public float Width => Max.X - Min.X;
        public float Height => Max.Y - Min.Y;
        public Rect Inflate(float by) => new(Min - new Vector2(by), Max + new Vector2(by));
        public bool Overlaps(Rect o, float tolerance) =>
            Min.X + tolerance < o.Max.X && o.Min.X + tolerance < Max.X && Min.Y + tolerance < o.Max.Y && o.Min.Y + tolerance < Max.Y;
        public bool Inside(Vector2 min, Vector2 max, float slack) =>
            Min.X >= min.X - slack && Min.Y >= min.Y - slack && Max.X <= max.X + slack && Max.Y <= max.Y + slack;
    }

    /// <summary>The card's glass frame (preview + padding), plus the info strip for the selected orbital card.</summary>
    private static Rect CardRect(in CardPose pose, float s, bool withInfo)
    {
        float pad = SolarSystemLayout.CardPadding * s * pose.Scale;
        float info = withInfo ? SolarSystemLayout.InfoStripHeight * s * pose.TypeScale : 0f;
        var half = pose.PreviewSize * 0.5f;
        return new Rect(pose.Center - half - new Vector2(pad), pose.Center + half + new Vector2(pad, pad + info));
    }

    /// <summary>Screen-space bounds of a card with yaw and depth, as the renderer projects it.</summary>
    private static Rect Projected(in CardPose pose, float s, LayoutResult layout, Vector2 viewport, bool deep)
    {
        var frame = new FrameConstants { Camera = new Vector4(0f, viewport.Y * (deep ? 1.35f : 1.9f), layout.Anchor.X, layout.Anchor.Y) };
        var half = pose.PreviewSize * 0.5f + new Vector2(SolarSystemLayout.CardPadding * s * pose.Scale);
        var p0 = new Vector4(pose.Center, 1f, 1f);
        var p2 = new Vector4(pose.Yaw, pose.Pitch, 0f, pose.Z);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var corner in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
        {
            var p = Projection.Project(corner * half, p0, p2, frame);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return new Rect(min, max);
    }

    private static float OverlapRatio(Rect a, Rect b)
    {
        float w = MathF.Min(a.Max.X, b.Max.X) - MathF.Max(a.Min.X, b.Min.X);
        float h = MathF.Min(a.Max.Y, b.Max.Y) - MathF.Max(a.Min.Y, b.Min.Y);
        if (w <= 0 || h <= 0) return 0f;
        return w * h / MathF.Min(a.Width * a.Height, b.Width * b.Height);
    }

    // ───────────────────────────── Solar System ─────────────────────────────

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Solar_system_keeps_the_selected_card_clear_and_everything_on_screen(float selected, float orbit)
    {
        var size = new CardSizing(selected, orbit);
        foreach (var mode in new[] { SwitcherMode.SolarSystem, SwitcherMode.OrbitMinimal })
        foreach (var screen in Screens)
        foreach (float expansion in new[] { 0f, 1f })
        {
            var engine = new SolarSystemLayout(mode == SwitcherMode.OrbitMinimal);
            var result = new LayoutResult();
            for (int n = 1; n <= 30; n++)
            {
                var ctx = Context(screen, size, mode, rotor: n / 2, expansion: expansion);
                engine.Compute(ctx, Items(n), result);
                float s = ctx.Space.Scale;
                int sel = n / 2;
                var center = CardRect(result.Poses[sel], s, withInfo: true);
                string at = $"{mode} {selected:0.00}/{orbit:0.00} n={n} on {screen} (expansion {expansion})";

                Assert.True(center.Inside(Vector2.Zero, screen, 1f), $"{at}: the selected card leaves the screen");
                float label = SolarSystemLayout.LabelSpace * s * expansion;
                float limit = n <= 16 ? 0.05f : 0.25f;
                for (int i = 0; i < n; i++)
                {
                    if (i == sel) continue;
                    var pose = result.Poses[i];
                    var rect = CardRect(pose, s, withInfo: false);
                    bool back = pose.Depth >= 0.5f;

                    // In front: never touches the selected card or its title. Behind: may tuck under its edge.
                    var keepOut = back ? center.Inflate(-SolarSystemLayout.BackTuck * s) : center;
                    Assert.False(keepOut.Overlaps(rect, SolarSystemLayout.Tolerance * s), $"{at}: card {i} covers the selected card");

                    var withLabel = back ? new Rect(rect.Min - new Vector2(0, label), rect.Max) : new Rect(rect.Min, rect.Max + new Vector2(0, label));
                    Assert.True(withLabel.Inside(Vector2.Zero, screen, 1f), $"{at}: card {i} (or its label) leaves the screen");

                    for (int j = i + 1; j < n; j++)
                    {
                        if (j == sel) continue;
                        float ratio = OverlapRatio(rect, CardRect(result.Poses[j], s, false));
                        Assert.True(ratio <= limit + 0.01f, $"{at}: cards {i} and {j} overlap {ratio:P0}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Default_size_keeps_the_designed_solar_system()
    {
        // The default size must look exactly as designed: nothing relaxed, automatic orbit count.
        var engine = new SolarSystemLayout();
        var result = new LayoutResult();
        foreach (var screen in Screens)
        {
            for (int n = 2; n <= 30; n++)
            {
                engine.Compute(Context(screen, CardSizing.Default), Items(n), result);
                Assert.Equal(SolarSystemLayout.AutoRingCount(n), engine.RingCount);
                Assert.Equal((0f, 0f, 0f, 1f), engine.Relaxation);
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    public void Presets_keep_the_automatic_orbit_count_without_shrinking(int n)
    {
        foreach (var preset in CardSizePresets.All)
        {
            var (selected, orbit) = CardSizePresets.Values(preset);
            var engine = new SolarSystemLayout();
            engine.Compute(Context(new Vector2(1920, 1080), new CardSizing(selected, orbit)), Items(n), new LayoutResult());
            Assert.Equal(SolarSystemLayout.AutoRingCount(n), engine.RingCount);
            Assert.Equal(0f, engine.Relaxation.Shrink);
        }
    }

    [Fact]
    public void Compact_and_huge_are_clearly_different()
    {
        var screen = new Vector2(1920, 1080);
        var (cs, co) = CardSizePresets.Values(CardSizePreset.Compact);
        var (hs, ho) = CardSizePresets.Values(CardSizePreset.Huge);
        var compact = new LayoutResult();
        var huge = new LayoutResult();
        new SolarSystemLayout().Compute(Context(screen, new CardSizing(cs, co)), Items(7), compact);
        new SolarSystemLayout().Compute(Context(screen, new CardSizing(hs, ho)), Items(7), huge);

        // Card 0 is selected; card 1 is the nearest planet.
        Assert.True(huge.Poses[0].PreviewSize.X / compact.Poses[0].PreviewSize.X > 1.6f, "selected card");
        Assert.True(huge.Poses[1].PreviewSize.X / compact.Poses[1].PreviewSize.X > 1.35f, "planets");
        // Orbits make room: the nearest planet sits farther out.
        Assert.True(Vector2.Distance(huge.Poses[1].Center, huge.Anchor) > Vector2.Distance(compact.Poses[1].Center, compact.Anchor) * 1.15f);
        // Orbit lines follow the planets.
        Assert.True(huge.Rings[0].Radii.X > compact.Rings[0].Radii.X);
    }

    [Fact]
    public void Selected_and_orbit_sizes_are_independent()
    {
        var screen = new Vector2(2560, 1440);
        var baseline = new LayoutResult();
        var focus = new LayoutResult();
        new SolarSystemLayout().Compute(Context(screen, CardSizing.Default), Items(7), baseline);
        new SolarSystemLayout().Compute(Context(screen, new CardSizing(1.35f, 0.85f)), Items(7), focus);

        Assert.Equal(1.35f, focus.Poses[0].PreviewSize.X / baseline.Poses[0].PreviewSize.X, 2);
        for (int i = 1; i < 7; i++)
            Assert.Equal(0.85f, focus.Poses[i].PreviewSize.X / baseline.Poses[i].PreviewSize.X, 2);
    }

    [Fact]
    public void Typography_grows_much_less_than_the_card()
    {
        var screen = new Vector2(1920, 1080);
        var baseline = new LayoutResult();
        var max = new LayoutResult();
        new SolarSystemLayout().Compute(Context(screen, CardSizing.Default), Items(5), baseline);
        new SolarSystemLayout().Compute(Context(screen, new CardSizing(MaxSelected, MaxOrbit)), Items(5), max);

        Assert.Equal(baseline.Poses[0].Scale, baseline.Poses[0].TypeScale, 4);
        float card = max.Poses[0].Scale / baseline.Poses[0].Scale;
        float text = max.Poses[0].TypeScale / baseline.Poses[0].TypeScale;
        Assert.True(card > 1.6f, $"card grew only {card:0.00}×");
        Assert.InRange(text, 1.05f, 1.2f);

        var min = new LayoutResult();
        new SolarSystemLayout().Compute(Context(screen, new CardSizing(MinSelected, MinOrbit)), Items(5), min);
        Assert.InRange(min.Poses[1].TypeScale / baseline.Poses[1].TypeScale, 0.8f, 0.9f);
    }

    [Fact]
    public void Very_large_cards_get_more_room_or_another_orbit()
    {
        // With the largest selected card the designed geometry would put planets onto it: the
        // solver must widen, gather, restructure or shrink — and the result is clean (see above).
        var engine = new SolarSystemLayout();
        engine.Compute(Context(new Vector2(1920, 1080), new CardSizing(MaxSelected, MaxOrbit)), Items(9), new LayoutResult());
        var (arc, stretch, shrink, growth) = engine.Relaxation;
        Assert.True(arc > 0 || stretch > 0 || shrink > 0 || engine.RingCount != SolarSystemLayout.AutoRingCount(9) || MathF.Abs(growth - 1f) > 0.01f);
    }

    [Fact]
    public void Ultrawide_space_is_used_before_shrinking()
    {
        var size = new CardSizing(MaxSelected, MaxOrbit);
        var wide = new SolarSystemLayout();
        var normal = new SolarSystemLayout();
        wide.Compute(Context(new Vector2(3440, 1440), size), Items(10), new LayoutResult());
        normal.Compute(Context(new Vector2(2560, 1440), size), Items(10), new LayoutResult());
        Assert.True(wide.Relaxation.Shrink <= normal.Relaxation.Shrink);
    }

    // ───────────────────────────── other layouts ─────────────────────────────

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rows_and_grid_keep_the_selected_card_clear_and_on_screen(float selected, float orbit)
    {
        var size = new CardSizing(selected, orbit);
        foreach (var mode in new[] { SwitcherMode.Carousel, SwitcherMode.CoverFlow, SwitcherMode.Grid })
        foreach (var screen in Screens)
        foreach (int n in new[] { 1, 2, 5, 9, 16, 30 })
        {
            var engine = LayoutFactory.Create(mode);
            var result = new LayoutResult();
            int sel = n / 2;
            var ctx = Context(screen, size, mode, rotor: sel);
            engine.Compute(ctx, Items(n), result);
            float s = ctx.Space.Scale;
            bool deep = mode != SwitcherMode.Grid;
            string at = $"{mode} {selected:0.00}/{orbit:0.00} n={n} on {screen}";

            var center = Projected(result.Poses[sel], s, result, screen, deep);
            Assert.True(center.Inside(Vector2.Zero, screen, 1f), $"{at}: the selected card leaves the screen");
            // The caption under the selected card (name and title) fits too.
            Assert.True(result.FocusBottom + 70f * s <= screen.Y, $"{at}: no room for the caption");

            for (int i = 0; i < n; i++)
            {
                if (i == sel) continue;
                var pose = result.Poses[i];
                var rect = Projected(pose, s, result, screen, deep);
                switch (mode)
                {
                    case SwitcherMode.Carousel when Math.Abs(i - sel) == 1:
                        Assert.False(center.Overlaps(rect, 2f * s), $"{at}: neighbour {i} covers the selected card");
                        break;
                    case SwitcherMode.CoverFlow when Math.Abs(i - sel) == 1:
                        // Side covers tuck under the flat centre cover (drawn behind it) but stay mostly visible.
                        Assert.True(pose.SortKey < result.Poses[sel].SortKey);
                        float outside = i > sel ? rect.Max.X - center.Max.X : center.Min.X - rect.Min.X;
                        Assert.True(outside >= rect.Width * 0.4f, $"{at}: cover {i} is hidden behind the selected card");
                        break;
                    case SwitcherMode.Grid:
                        // Cells (with their labels) never touch the lifted selected card or each other.
                        float label = 34f * s;
                        var cell = new Rect(rect.Min, rect.Max + new Vector2(0, label));
                        Assert.False(new Rect(center.Min, center.Max + new Vector2(0, label)).Overlaps(cell, 1f), $"{at}: cell {i} touches the selected card");
                        Assert.True(cell.Inside(Vector2.Zero, screen, 1f), $"{at}: cell {i} leaves the screen");
                        for (int j = i + 1; j < n; j++)
                        {
                            if (j == sel) continue;
                            var other = Projected(result.Poses[j], s, result, screen, deep);
                            Assert.False(cell.Overlaps(new Rect(other.Min, other.Max + new Vector2(0, label)), 1f), $"{at}: cells {i} and {j} touch");
                        }
                        break;
                }
            }
        }
    }

    [Theory]
    [InlineData(SwitcherMode.Carousel)]
    [InlineData(SwitcherMode.CoverFlow)]
    [InlineData(SwitcherMode.Grid)]
    public void Rows_and_grid_follow_both_sizes(SwitcherMode mode)
    {
        var screen = new Vector2(1920, 1080);
        var small = new LayoutResult();
        var large = new LayoutResult();
        LayoutFactory.Create(mode).Compute(Context(screen, new CardSizing(0.85f, 0.7f), mode, rotor: 4), Items(9), small);
        LayoutFactory.Create(mode).Compute(Context(screen, new CardSizing(1.5f, 1.05f), mode, rotor: 4), Items(9), large);
        Assert.True(large.Poses[4].PreviewSize.X > small.Poses[4].PreviewSize.X * 1.3f, "selected card");
        Assert.True(large.Poses[5].PreviewSize.X > small.Poses[5].PreviewSize.X * 1.2f, "other cards");
        // In every layout the selected card is the largest.
        Assert.True(large.Poses[4].PreviewSize.X > large.Poses[5].PreviewSize.X);
    }

    [Theory]
    [InlineData(SwitcherMode.SolarSystem)]
    [InlineData(SwitcherMode.Carousel)]
    [InlineData(SwitcherMode.CoverFlow)]
    [InlineData(SwitcherMode.Grid)]
    public void Motion_stays_continuous_at_any_size(SwitcherMode mode)
    {
        var screen = new Vector2(2560, 1440);
        foreach (var size in new[] { new CardSizing(MinSelected, MinOrbit), new CardSizing(MaxSelected, MaxOrbit), new CardSizing(MaxSelected, MinOrbit) })
        foreach (int n in new[] { 3, 8, 13 })
        {
            var engine = LayoutFactory.Create(mode);
            var a = new LayoutResult();
            var b = new LayoutResult();
            var items = Items(n);
            double end = engine.Wraps ? n + 1 : n - 1;
            for (double p = 0; p < end; p += 0.01)
            {
                engine.Compute(Context(screen, size, mode, p), items, a);
                engine.Compute(Context(screen, size, mode, p + 0.01), items, b);
                for (int i = 0; i < n; i++)
                {
                    float jump = Vector2.Distance(a.Poses[i].Center, b.Poses[i].Center);
                    Assert.True(jump < 60f, $"{mode} {size} n={n} card {i} jumped {jump:0}px at rotor {p:0.00}");
                }
            }
        }
    }

    // ───────────────────────────── live changes ─────────────────────────────

    private sealed class Resources : ISceneResources
    {
        public bool HasBackdrop => true;
        public PreviewInfo GetPreview(long windowHandle) => new(true, new Vector4(0, 0, 1, 1), true, 1.6f);
        public bool HasIcon(string appId) => true;
    }

    private static (SwitcherSession Session, SwitcherAnimator Animator, SceneInput Input, ILayoutEngine Engine) Setup(int windows, bool reduced = false,
        SwitcherMode mode = SwitcherMode.SolarSystem)
    {
        var settings = new FlowSwitchSettings();
        var session = new SwitcherSession(TestData.Windows(windows), new SessionOptions { Grouping = GroupingMode.Off });
        var motion = MotionProfile.Resolve(settings.Animation, settings.Solar, reduced) with { IdleAmount = 0f, BreathingAmplitude = 0f };
        var animator = new SwitcherAnimator();
        animator.Begin(session, motion);
        var ctx = new LayoutContext { Space = DesignSpace.For(new Vector2(2560, 1440)), Motion = motion, Solar = settings.Solar, Mode = mode };
        var input = new SceneInput
        {
            Session = session,
            Animator = animator,
            Layout = ctx,
            Palette = ScenePalette.Resolve(settings.Appearance, settings.Solar, mode),
            Quality = QualityProfile.High,
            Resources = new Resources(),
        };
        return (session, animator, input, LayoutFactory.Create(mode));
    }

    private static void Step(SwitcherSession s, SwitcherAnimator a, SceneInput input, ILayoutEngine engine, float seconds)
    {
        for (float t = 0; t < seconds - 1e-4f; t += 1f / 120f)
        {
            s.Tick(1f / 120f);
            a.Update(1f / 120f, s, engine, input.Layout, input.Resources, input.Palette, null);
        }
    }

    [Fact]
    public void Changing_the_size_glides_in_about_200_ms()
    {
        var (s, a, input, engine) = Setup(7);
        Step(s, a, input, engine, 1f);
        float from = a.Cards[s.SelectedIndex].Pose.PreviewSize.X;
        var (hs, ho) = CardSizePresets.Values(CardSizePreset.Huge);
        input.Layout.TargetCardSize = new CardSizing(hs, ho);

        Step(s, a, input, engine, 1f / 120f);
        Assert.True(a.IsAnimating);
        float firstFrame = a.Cards[s.SelectedIndex].Pose.PreviewSize.X;
        Step(s, a, input, engine, 0.3f);
        float to = a.Cards[s.SelectedIndex].Pose.PreviewSize.X;
        Assert.True(to > from * 1.4f);
        // No snap: the first frame moves only a little of the way.
        Assert.True((firstFrame - from) / (to - from) < 0.1f);

        // Replay and sample the progress over time.
        (s, a, input, engine) = Setup(7);
        Step(s, a, input, engine, 1f);
        input.Layout.TargetCardSize = new CardSizing(hs, ho);
        Step(s, a, input, engine, 0.075f);
        float early = (a.Cards[s.SelectedIndex].Pose.PreviewSize.X - from) / (to - from);
        Step(s, a, input, engine, 0.175f);
        float settled = (a.Cards[s.SelectedIndex].Pose.PreviewSize.X - from) / (to - from);
        Assert.InRange(early, 0.25f, 0.9f);
        Assert.True(settled > 0.97f, $"only {settled:P0} of the way after 250 ms");
    }

    [Fact]
    public void Reduced_motion_applies_the_size_at_once()
    {
        var (s, a, input, engine) = Setup(7, reduced: true);
        Step(s, a, input, engine, 0.6f);
        float from = a.Cards[s.SelectedIndex].Pose.PreviewSize.X;
        input.Layout.TargetCardSize = new CardSizing(1.5f, 1.05f);
        Step(s, a, input, engine, 1f / 120f);
        Assert.Equal(1.5f, a.Cards[s.SelectedIndex].Pose.PreviewSize.X / from, 2);
    }

    [Fact]
    public void A_new_session_starts_at_the_configured_size()
    {
        var (s, a, input, engine) = Setup(5);
        input.Layout.TargetCardSize = new CardSizing(1.5f, 1.05f);
        Step(s, a, input, engine, 0.6f);
        float big = a.Cards[s.SelectedIndex].Pose.PreviewSize.X;

        var (s2, a2, input2, engine2) = Setup(5);
        Step(s2, a2, input2, engine2, 0.6f);
        Assert.Equal(1.5f, big / a2.Cards[s2.SelectedIndex].Pose.PreviewSize.X, 2);
    }

    [Fact]
    public void Resizing_while_open_moves_cards_without_jumping()
    {
        // 9 windows: two orbits at the default size, three at the largest (see the solver). Cards
        // change orbit and travel far, but they must glide: a teleport covers the same distance in
        // one frame at any frame rate, smooth motion covers proportionally less at a higher one.
        (float Step, int Before, int After) Run(int fps)
        {
            var (s, a, input, engine) = Setup(9);
            Step(s, a, input, engine, 1f);
            int before = a.Layout.Topology;
            input.Layout.TargetCardSize = new CardSizing(MaxSelected, MaxOrbit);
            var last = a.Cards.Select(c => c.Pose.Center).ToArray();
            float worst = 0f;
            for (int frame = 0; frame < fps; frame++)
            {
                s.Tick(1f / fps);
                a.Update(1f / fps, s, engine, input.Layout, input.Resources, input.Palette, null);
                for (int i = 0; i < a.Cards.Count; i++)
                {
                    worst = MathF.Max(worst, Vector2.Distance(last[i], a.Cards[i].Pose.Center));
                    last[i] = a.Cards[i].Pose.Center;
                }
            }
            return (worst, before, a.Layout.Topology);
        }

        var at120 = Run(120);
        var at480 = Run(480);
        Assert.NotEqual(at120.Before, at120.After);
        Assert.True(at480.Step < at120.Step / 4f * 1.5f + 2f,
            $"largest step {at480.Step:0}px at 480 Hz vs {at120.Step:0}px at 120 Hz: something jumps instead of moving");
    }

    [Fact]
    public void Hit_box_follows_the_card_size()
    {
        // A point inside the huge selected card but outside the compact one.
        var (s, a, input, engine) = Setup(6);
        input.Layout.TargetCardSize = new CardSizing(1.6f, 1f);
        Step(s, a, input, engine, 1f);
        var composer = new SceneComposer();
        composer.Compose(input);
        var selected = a.Cards[s.SelectedIndex].Pose;
        var probe = selected.Center + new Vector2(selected.PreviewSize.X * 0.47f, 0f);
        Assert.Equal(HitKind.Card, composer.HitTest(probe).Kind);
        Assert.Equal(s.Selected!.Key, composer.HitTest(probe).Key);

        var (s2, a2, input2, engine2) = Setup(6);
        input2.Layout.TargetCardSize = new CardSizing(0.85f, 0.7f);
        Step(s2, a2, input2, engine2, 1f);
        var composer2 = new SceneComposer();
        composer2.Compose(input2);
        Assert.NotEqual(s2.Selected!.Key, composer2.HitTest(probe).Key);
    }

    [Fact]
    public void Glow_and_shadow_scale_with_the_card_but_text_barely_does()
    {
        float Measure(CardSizing size, Func<DrawList, CardVisual, float> metric)
        {
            var (s, a, input, engine) = Setup(5);
            input.Layout.TargetCardSize = size;
            Step(s, a, input, engine, 1f);
            return metric(new SceneComposer().Compose(input), a.Cards[s.SelectedIndex]);
        }

        // Card shader: P[1].xy is the glass half-size, P[8].x the shadow radius.
        static float Glass(DrawList list, CardVisual c) => list.Commands.ToArray().Where(x => x.Shader == ShaderKind.Card)
            .Select(x => x.P[1].X).Max();
        static float Shadow(DrawList list, CardVisual c) => list.Commands.ToArray().Where(x => x.Shader == ShaderKind.Card)
            .OrderByDescending(x => x.P[1].X).First().P[8].X;
        // The selected card's app name in the info strip: P[7].w maps raster pixels to the card.
        static float NameScale(DrawList list, CardVisual c) => list.Commands.ToArray()
            .Where(x => x.Texture.Kind == TextureKind.Text && x.Texture.Text!.Text == c.Entry.Primary.App.DisplayName)
            .Select(x => x.P[7].W).Max();
        static float Glow(DrawList list, CardVisual c) => list.Commands.ToArray().Where(x => x.Shader == ShaderKind.Glow)
            .Select(x => x.P[1].X).Max();

        var normal = CardSizing.Default;
        var big = new CardSizing(1.6f, 1f);
        Assert.Equal(1.6f, Measure(big, Glass) / Measure(normal, Glass), 1);
        Assert.Equal(1.6f, Measure(big, Shadow) / Measure(normal, Shadow), 1);
        Assert.True(Measure(big, Glow) / Measure(normal, Glow) > 1.4f);
        Assert.InRange(Measure(big, NameScale) / Measure(normal, NameScale), 1.05f, 1.15f);
    }

    // ───────────────────────────── settings ─────────────────────────────

    [Fact]
    public void Sizes_default_to_the_designed_size_and_are_clamped()
    {
        var defaults = new FlowSwitchSettings();
        Assert.Equal(1f, defaults.Appearance.SelectedCardSize);
        Assert.Equal(1f, defaults.Appearance.OrbitCardSize);
        Assert.Equal(CardSizePreset.Default, CardSizePresets.Match(defaults.Appearance.SelectedCardSize, defaults.Appearance.OrbitCardSize));

        var s = new FlowSwitchSettings();
        s.Appearance.SelectedCardSize = 5f;
        s.Appearance.OrbitCardSize = 0.1f;
        s.Normalize();
        Assert.Equal(MaxSelected, s.Appearance.SelectedCardSize);
        Assert.Equal(MinOrbit, s.Appearance.OrbitCardSize);
        s.Appearance.SelectedCardSize = float.NaN;
        s.Normalize();
        Assert.Equal(1f, s.Appearance.SelectedCardSize);
    }

    [Fact]
    public void The_single_card_size_of_older_settings_files_is_migrated()
    {
        const string json = """{ "version": 1, "appearance": { "cardSize": 1.2 } }""";
        var settings = SettingsSerializer.Deserialize(json);
        Assert.Equal(1.2f, settings.Appearance.SelectedCardSize, 3);
        Assert.Equal(1.2f, settings.Appearance.OrbitCardSize, 3);
        Assert.Null(settings.Appearance.CardSize);
        string saved = SettingsSerializer.Serialize(settings);
        Assert.DoesNotContain("\"cardSize\"", saved);
        Assert.Contains("\"selectedCardSize\"", saved);
    }

    [Fact]
    public void Presets_round_trip_and_hand_tuned_sizes_are_custom()
    {
        foreach (var preset in CardSizePresets.All)
        {
            var appearance = new AppearanceSettings();
            CardSizePresets.Apply(appearance, preset);
            Assert.Equal(preset, CardSizePresets.Match(appearance.SelectedCardSize, appearance.OrbitCardSize));
            // Presets lie within the slider ranges.
            Assert.InRange(appearance.SelectedCardSize, MinSelected, MaxSelected);
            Assert.InRange(appearance.OrbitCardSize, MinOrbit, MaxOrbit);
        }
        Assert.Equal(CardSizePreset.Custom, CardSizePresets.Match(1.35f, 0.85f));

        // Each preset is larger than the previous one.
        var sizes = CardSizePresets.All.Select(CardSizePresets.Values).ToArray();
        for (int i = 1; i < sizes.Length; i++) Assert.True(sizes[i].Selected > sizes[i - 1].Selected);
    }
}
