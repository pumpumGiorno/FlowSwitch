using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>Cards stand on a horizontal cylinder; the selected card faces the viewer.</summary>
public sealed class CarouselLayout : ILayoutEngine
{
    private const float BoxWidth = 500f;
    private const float BoxHeight = 300f;
    private const float Radius = 1000f;
    private const float CenterY = 380f;

    public SwitcherMode Mode => SwitcherMode.Carousel;

    public bool Wraps => false;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var motion = ctx.Motion;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;
        float step = LayoutMath.Deg(n <= 8 ? 34f : n <= 14 ? 30f : 26f);
        float breathing = 1f + motion.BreathingAmplitude * MathF.Sin(ctx.Time * MathF.Tau / motion.BreathingPeriod);
        var anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, CenterY) - parallax * 10f);

        result.Anchor = anchor;
        result.FocusBottom = anchor.Y + sp.Px(BoxHeight * ctx.CardSize * 0.5f + 34f);

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            // A row, not a ring: going past the last window scrolls back instead of wrapping.
            double d = i - ctx.RotorPosition;
            float a = (float)Math.Abs(d);
            float theta = (float)d * step;
            float at = MathF.Abs(theta);
            float focus = 1f - Easing.Smootherstep(Math.Min(1f, a));
            Vector2 box = LayoutMath.Fit(items[i].Aspect, BoxWidth * ctx.CardSize, BoxHeight * ctx.CardSize, 1.2f, 2.0f);

            float x = MathF.Sin(theta) * Radius * 1.08f;
            float z = (1f - MathF.Cos(theta)) * Radius;
            float bob = MathF.Sin(ctx.Time * MathF.Tau / 7f + i * 1.7f) * 2f * motion.IdleAmount * (1f - focus);
            float scale = Easing.Lerp(1f, breathing, focus);

            pose.Center = anchor + sp.Scale * new Vector2(x, bob);
            pose.Z = sp.Px(z);
            // Cards turn only part of the way with the cylinder, so side cards still face the viewer.
            pose.Yaw = theta * 0.55f * motion.DepthIntensity;
            pose.Pitch = 0f;
            pose.Scale = box.X / (SolarSystemLayout.CenterBoxWidth * ctx.CardSize) * scale;
            pose.PreviewSize = box * sp.Scale * scale;
            pose.Brightness = Easing.Lerp(1f, 0.42f, Math.Min(1f, at / (MathF.PI * 0.5f)));
            pose.Opacity = 1f - Easing.Smoothstep(LayoutMath.Deg(78f), LayoutMath.Deg(112f), at);
            pose.Blur = Math.Min(1f, a * 0.22f) * motion.DepthIntensity;
            pose.Depth = Math.Min(1f, z / (Radius * 2f));
            pose.Focus = focus;
            pose.Glow = Easing.Lerp(0.22f, 1f, focus);
            pose.InfoAlpha = 0f;
            pose.LabelAlpha = ctx.Expansion * (1f - focus) * pose.Brightness;
            pose.Reflection = 0.16f;
            pose.SortKey = -z * 0.001f + focus * 0.01f;
        }
    }
}
