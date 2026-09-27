using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>Cards stand on a horizontal cylinder; the selected card faces the viewer.</summary>
/// <remarks>
/// Card sizes: the selected card uses the selected size, the others the orbit size. The first
/// step around the cylinder opens up as far as needed to keep the selected card clear, and the
/// following steps scale with the side cards, so larger cards show fewer neighbours instead of
/// piling up.
/// </remarks>
public sealed class CarouselLayout : ILayoutEngine
{
    private const float BoxWidth = 500f;
    private const float BoxHeight = 300f;
    private const float Radius = 1000f;
    private const float CenterY = 380f;
    private const float CaptionSpace = 34f;
    private const float Gap = 14f;

    private (CardSizing Size, int N, float Depth, Vector2 Min, Vector2 Max) _cacheKey = (default, -1, 0, default, default);
    private float _selectedK = 1f, _orbitK = 1f, _firstStep, _step;

    public SwitcherMode Mode => SwitcherMode.Carousel;

    public bool Wraps => false;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var motion = ctx.Motion;
        var size = ctx.CardSize;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;
        Configure(n, ctx);
        float breathing = 1f + motion.BreathingAmplitude * MathF.Sin(ctx.Time * MathF.Tau / motion.BreathingPeriod);
        var anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, CenterY) - parallax * 10f);

        result.Anchor = anchor;
        result.FocusBottom = anchor.Y + sp.Px(BoxHeight * _selectedK * 0.5f + CaptionSpace);
        result.Topology = 0;

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            // A row, not a ring: going past the last window scrolls back instead of wrapping.
            double d = i - ctx.RotorPosition;
            float a = (float)Math.Abs(d);
            float theta = MathF.Sign((float)d) * Angle(a);
            float at = MathF.Abs(theta);
            float focus = 1f - Easing.Smootherstep(Math.Min(1f, a));
            float k = Easing.Lerp(_orbitK, _selectedK, focus);
            Vector2 box = LayoutMath.Fit(items[i].Aspect, BoxWidth * k, BoxHeight * k, 1.2f, 2.0f);

            float x = MathF.Sin(theta) * Radius * 1.08f;
            float z = (1f - MathF.Cos(theta)) * Radius;
            float bob = MathF.Sin(ctx.Time * MathF.Tau / 7f + i * 1.7f) * 2f * motion.IdleAmount * (1f - focus);
            float scale = Easing.Lerp(1f, breathing, focus);

            pose.Center = anchor + sp.Scale * new Vector2(x, bob);
            pose.Z = sp.Px(z);
            // Cards turn only part of the way with the cylinder, so side cards still face the viewer.
            pose.Yaw = theta * 0.55f * motion.DepthIntensity;
            pose.Pitch = 0f;
            pose.Scale = box.X / SolarSystemLayout.CenterBoxWidth * scale;
            pose.TypeScale = pose.Scale * CardSizing.TypeFactor(size.At(focus));
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

    /// <summary>Angle around the cylinder of a card <paramref name="a"/> places away from the selected one.</summary>
    private float Angle(float a) => a <= 1f ? a * _firstStep : _firstStep + (a - 1f) * _step;

    private void Configure(int n, LayoutContext ctx)
    {
        var size = ctx.CardSize;
        var key = (size, n, ctx.Motion.DepthIntensity, ctx.Space.VisibleMin, ctx.Space.VisibleMax);
        if (key == _cacheKey) return;
        _cacheKey = key;

        var min = ctx.Space.VisibleMin;
        var max = ctx.Space.VisibleMax;
        // The selected card and the caption under it always fit the monitor.
        float fit = MathF.Min(MathF.Min((CenterY - min.Y - 40f) / (BoxHeight * 0.5f), (max.Y - CenterY - CaptionSpace - 110f) / (BoxHeight * 0.5f)),
                              (max.X - min.X - 80f) / BoxWidth);
        _selectedK = MathF.Min(size.Selected, fit);
        _orbitK = MathF.Min(size.Orbit, fit);

        float designed = LayoutMath.Deg(n <= 8 ? 34f : n <= 14 ? 30f : 26f);
        // Larger side cards need proportionally more room around the cylinder.
        _step = designed * Math.Clamp(_orbitK, 0.7f, 1.5f);

        // First step: far enough that the neighbour never covers the selected card.
        float focal = (max.Y - min.Y) * 1.35f;
        float centerHalf = (BoxWidth * 0.5f + SolarSystemLayout.CardPadding) * _selectedK;
        float sideHalf = (BoxWidth * 0.5f + SolarSystemLayout.CardPadding) * _orbitK;
        float needed = LayoutMath.Deg(80f);
        for (float deg = 8f; deg <= 80f; deg += 0.25f)
        {
            float theta = LayoutMath.Deg(deg);
            float w = (focal + (1f - MathF.Cos(theta)) * Radius) / focal;
            float inner = (MathF.Sin(theta) * Radius * 1.08f - sideHalf * MathF.Cos(theta * 0.55f * ctx.Motion.DepthIntensity)) / w;
            if (inner < centerHalf + Gap) continue;
            needed = theta;
            break;
        }
        _firstStep = MathF.Max(_step, needed);
    }
}
