using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

public class LayoutTests
{
    private static readonly Vector2[] Screens =
    {
        new(1920, 1080), new(2560, 1440), new(3440, 1440), new(1366, 768), new(2560, 1600), new(3840, 2160),
    };

    private static LayoutContext Context(Vector2 screen, double rotor, SwitcherMode mode = SwitcherMode.SolarSystem) => new()
    {
        Space = DesignSpace.For(screen),
        RotorPosition = rotor,
        Motion = MotionProfile.Smooth with { IdleAmount = 0f, BreathingAmplitude = 0f },
        Solar = new SolarSystemSettings(),
        Mode = mode,
    };

    private static LayoutItem[] Items(int n) => Enumerable.Range(0, n).Select(i => new LayoutItem(i % 3 == 0 ? 1.78f : 1.6f, false, 1)).ToArray();

    private static (Vector2 Min, Vector2 Max) CardRect(in CardPose pose, float designScale, bool withInfo)
    {
        float pad = SolarSystemLayout.CardPadding * designScale * pose.Scale;
        float info = withInfo ? SolarSystemLayout.InfoStripHeight * designScale * pose.Scale : 0f;
        var half = pose.PreviewSize * 0.5f;
        var min = pose.Center - half - new Vector2(pad);
        var max = pose.Center + half + new Vector2(pad, pad + info);
        // Account for the ±12° yaw foreshortening and the label under orbit cards.
        return (min, max);
    }

    private static bool Overlaps((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b, float tolerance) =>
        a.Min.X + tolerance < b.Max.X && b.Min.X + tolerance < a.Max.X &&
        a.Min.Y + tolerance < b.Max.Y && b.Min.Y + tolerance < a.Max.Y;

    [Fact]
    public void Selected_card_sits_in_the_centre_at_full_focus()
    {
        var engine = new SolarSystemLayout();
        var result = new LayoutResult();
        engine.Compute(Context(new Vector2(2560, 1440), 1), Items(6), result);
        Assert.Equal(1f, result.Poses[1].Focus, 3);
        Assert.True(result.Poses.Where((_, i) => i != 1).All(p => p.Focus < 0.01f));
    }

    [Fact]
    public void Planets_never_cover_the_selected_card_and_stay_on_screen()
    {
        var engine = new SolarSystemLayout();
        var result = new LayoutResult();
        foreach (var screen in Screens)
        {
            for (int n = 2; n <= 30; n++)
            {
                var ctx = Context(screen, 0);
                engine.Compute(ctx, Items(n), result);
                float s = ctx.Space.Scale;
                var center = CardRect(result.Poses[0], s, withInfo: true);
                for (int i = 1; i < n; i++)
                {
                    var pose = result.Poses[i];
                    var rect = CardRect(pose, s, withInfo: false);
                    // Cards on the far side may tuck slightly behind the centre card (depth cue),
                    // but never in front of it and never covering more than a sliver.
                    if (pose.Depth < 0.5f)
                        Assert.False(Overlaps(center, rect, 2f * s), $"n={n} card {i} overlaps the centre card on {screen}");
                    else
                        Assert.False(Overlaps(Shrink(center, 40f * s), rect, 2f * s), $"n={n} back card {i} hides the centre card on {screen}");

                    Assert.True(rect.Min.X >= -1 && rect.Max.X <= screen.X + 1, $"n={n} card {i} off screen horizontally on {screen}");
                    Assert.True(rect.Min.Y >= -1 && rect.Max.Y <= screen.Y + 1, $"n={n} card {i} off screen vertically on {screen}");
                }
            }
        }
    }

    private static (Vector2 Min, Vector2 Max) Shrink((Vector2 Min, Vector2 Max) r, float by) => (r.Min + new Vector2(by), r.Max - new Vector2(by));

    [Theory]
    [InlineData(SwitcherMode.SolarSystem)]
    [InlineData(SwitcherMode.Carousel)]
    [InlineData(SwitcherMode.CoverFlow)]
    [InlineData(SwitcherMode.Grid)]
    public void Motion_is_continuous_including_the_wrap_around(SwitcherMode mode)
    {
        var engine = LayoutFactory.Create(mode);
        foreach (int n in new[] { 2, 3, 5, 8, 9, 12, 17 })
        {
            var a = new LayoutResult();
            var b = new LayoutResult();
            var items = Items(n);
            var screen = new Vector2(2560, 1440);
            double end = engine.Wraps ? n + 1 : n - 1;
            for (double p = 0; p < end; p += 0.01)
            {
                engine.Compute(Context(screen, p), items, a);
                engine.Compute(Context(screen, p + 0.01), items, b);
                for (int i = 0; i < n; i++)
                {
                    float jump = Vector2.Distance(a.Poses[i].Center, b.Poses[i].Center);
                    Assert.True(jump < 60f, $"{mode} n={n} card {i} jumped {jump:0}px at rotor {p:0.00}");
                }
            }
        }
    }

    [Fact]
    public void Orbit_count_follows_window_count()
    {
        Assert.True(new SolarSystemLayout().Wraps);
        Assert.False(new CoverFlowLayout().Wraps);
        Assert.Equal(1, SolarSystemLayout.AutoRingCount(5));
        Assert.Equal(1, SolarSystemLayout.AutoRingCount(9));
        Assert.Equal(2, SolarSystemLayout.AutoRingCount(12));
        Assert.Equal(3, SolarSystemLayout.AutoRingCount(20));
        Assert.Equal(4, SolarSystemLayout.AutoRingCount(40));
    }

    [Fact]
    public void Grid_highlights_exactly_the_selection()
    {
        var engine = new GridLayout();
        var result = new LayoutResult();
        engine.Compute(Context(new Vector2(1920, 1080), 3, SwitcherMode.Grid), Items(9), result);
        Assert.Equal(1f, result.Poses[3].Focus, 3);
        Assert.Equal(1, result.Poses.Count(p => p.Focus > 0.5f));
    }

    [Fact]
    public void Design_space_keeps_proportions_on_any_screen()
    {
        var a = DesignSpace.For(new Vector2(1920, 1080));
        var b = DesignSpace.For(new Vector2(3840, 2160));
        Assert.Equal(a.Scale * 2f, b.Scale, 4);
        var ultrawide = DesignSpace.For(new Vector2(3440, 1440));
        Assert.True(ultrawide.Origin.X > 0);
        Assert.Equal(0f, ultrawide.Origin.Y, 3);
    }
}
