using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>The classic Cover Flow: a flat card in the middle, angled stacks on either side, a glossy floor.</summary>
/// <remarks>
/// Card sizes: the flat centre cover uses the selected size, the angled stacks the orbit size. The
/// gap to the stacks grows with both, so the stacks keep tucking under the centre cover by the
/// same amount, and every cover stands on the centre cover's floor line.
/// </remarks>
public sealed class CoverFlowLayout : ILayoutEngine
{
    private const float BoxWidth = 470f;
    private const float BoxHeight = 300f;
    private const float CenterGap = 300f;
    private const float StackSpacing = 76f;
    private const float StackDepth = 230f;
    private const float CenterY = 360f;
    /// <summary>Room under the centre cover for its reflection and the caption.</summary>
    private const float FloorSpace = 110f;

    public SwitcherMode Mode => SwitcherMode.CoverFlow;

    public bool Wraps => false;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var motion = ctx.Motion;
        var size = ctx.CardSize;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;
        float angle = LayoutMath.Deg(62f) * Math.Clamp(motion.DepthIntensity, 0.3f, 1.2f);
        float breathing = 1f + motion.BreathingAmplitude * MathF.Sin(ctx.Time * MathF.Tau / motion.BreathingPeriod);
        var anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, CenterY) - parallax * 10f);

        // The centre cover, its reflection and the caption always fit the monitor.
        var min = sp.VisibleMin;
        var max = sp.VisibleMax;
        float fit = MathF.Min(MathF.Min((CenterY - min.Y - 40f) / (BoxHeight * 0.5f), (max.Y - CenterY - FloorSpace - 70f) / (BoxHeight * 0.5f)),
                              (max.X - min.X - 80f) / BoxWidth);
        float selectedK = MathF.Min(size.Selected, fit);
        float orbitK = MathF.Min(size.Orbit, fit);
        // The stacks move out with the centre cover's edge (as seen at their depth), so they keep
        // tucking under it by the same share of their width at any size.
        float focal = (max.Y - min.Y) * 1.35f;
        float centerGap = CenterGap + (BoxWidth * 0.5f + SolarSystemLayout.CardPadding) * (selectedK - 1f) * (focal + StackDepth) / focal;
        float stackSpacing = StackSpacing * orbitK;
        float floor = BoxHeight * selectedK * 0.5f;

        result.Anchor = anchor;
        result.FocusBottom = anchor.Y + sp.Px(floor + FloorSpace);
        result.Topology = 0;

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            // A row, not a ring: going past the last window scrolls back instead of wrapping.
            double d = i - ctx.RotorPosition;
            float a = (float)Math.Abs(d);
            float sign = MathF.Sign((float)d);
            float t = Easing.Smootherstep(Math.Min(1f, a));
            float focus = 1f - t;
            float k = Easing.Lerp(orbitK, selectedK, focus);
            Vector2 box = LayoutMath.Fit(items[i].Aspect, BoxWidth * k, BoxHeight * k, 1.2f, 1.9f);

            float x = sign * (t * centerGap + MathF.Max(0f, a - 1f) * stackSpacing);
            float z = t * StackDepth;
            float scale = Easing.Lerp(1f, breathing, focus);

            // Covers share a common floor: align bottoms with the centre cover.
            float y = floor - box.Y * 0.5f;

            pose.Center = anchor + sp.Scale * new Vector2(x, y);
            pose.Z = sp.Px(z);
            pose.Yaw = -sign * t * angle;
            pose.Pitch = 0f;
            pose.Scale = box.X / SolarSystemLayout.CenterBoxWidth * scale;
            pose.TypeScale = pose.Scale * CardSizing.TypeFactor(size.At(focus));
            pose.PreviewSize = box * sp.Scale * scale;
            pose.Brightness = Easing.Lerp(1f, 0.6f, t) * (1f - 0.05f * MathF.Max(0f, a - 1f));
            pose.Opacity = 1f - Easing.Smoothstep(5.5f, 7.5f, a);
            pose.Blur = Math.Min(1f, MathF.Max(0f, a - 1f) * 0.12f) * motion.DepthIntensity;
            pose.Depth = Math.Min(1f, a / 6f);
            pose.Focus = focus;
            pose.Glow = Easing.Lerp(0.18f, 1f, focus);
            pose.InfoAlpha = 0f;
            pose.LabelAlpha = 0f;
            pose.Reflection = 0.3f;
            pose.SortKey = -a;
        }
    }
}
