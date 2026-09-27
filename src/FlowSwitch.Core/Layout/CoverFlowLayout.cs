using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>The classic Cover Flow: a flat card in the middle, angled stacks on either side, a glossy floor.</summary>
public sealed class CoverFlowLayout : ILayoutEngine
{
    private const float BoxWidth = 470f;
    private const float BoxHeight = 300f;
    private const float CenterGap = 300f;
    private const float StackSpacing = 76f;
    private const float StackDepth = 230f;
    private const float CenterY = 360f;

    public SwitcherMode Mode => SwitcherMode.CoverFlow;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var motion = ctx.Motion;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;
        float angle = LayoutMath.Deg(62f) * Math.Clamp(motion.DepthIntensity, 0.3f, 1.2f);
        float breathing = 1f + motion.BreathingAmplitude * MathF.Sin(ctx.Time * MathF.Tau / motion.BreathingPeriod);
        var anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, CenterY) - parallax * 10f);

        result.Anchor = anchor;
        result.FocusBottom = anchor.Y + sp.Px(BoxHeight * ctx.CardSize * 0.5f + 110f);

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            double d = n == 1 ? 0 : OrbitalRotor.WrapOffset(i - ctx.RotorPosition, n);
            float a = (float)Math.Abs(d);
            float sign = MathF.Sign((float)d);
            float t = Easing.Smootherstep(Math.Min(1f, a));
            float focus = 1f - t;
            Vector2 box = LayoutMath.Fit(items[i].Aspect, BoxWidth * ctx.CardSize, BoxHeight * ctx.CardSize, 1.2f, 1.9f);

            float x = sign * (t * CenterGap + MathF.Max(0f, a - 1f) * StackSpacing);
            float z = t * StackDepth;
            float scale = Easing.Lerp(1f, breathing, focus);

            // Covers share a common floor: align bottoms.
            float y = (BoxHeight * ctx.CardSize - box.Y) * 0.5f;

            pose.Center = anchor + sp.Scale * new Vector2(x, y);
            pose.Z = sp.Px(z);
            pose.Yaw = -sign * t * angle;
            pose.Pitch = 0f;
            pose.Scale = box.X / (SolarSystemLayout.CenterBoxWidth * ctx.CardSize) * scale;
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
