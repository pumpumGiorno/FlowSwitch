using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>
/// The signature layout: the selected window sits in the centre, the others travel on elliptical
/// orbits around it.
/// </summary>
/// <remarks>
/// <para>
/// Every card's pose is a pure function of the continuous rotor position, which is why rapid
/// Tab presses read as one rotating body instead of stacked animations.
/// </para>
/// <para>
/// Items are laid out along a continuous path per side (right side = upcoming in MRU order,
/// left side = recently passed). The path starts in the centre, leaves towards the front of the
/// inner orbit, runs around it to the back, transfers to the next orbit, runs to the front of that
/// orbit, and so on — a serpentine spiral. Both sides meet at the end of the last orbit, so
/// wrapping from the last window to the first stays continuous.
/// </para>
/// <para>
/// Resting slots are placed on the flanks of each orbit (not straight in front of or behind the
/// centre card), so no planet ever covers the selected window or its title.
/// </para>
/// </remarks>
public sealed class SolarSystemLayout : ILayoutEngine
{
    public const float CenterBoxWidth = 500f;
    public const float CenterBoxHeight = 300f;
    public const float CenterY = 410f;
    public const float CardPadding = 12f;
    public const float InfoStripHeight = 64f;
    private const float OrbitCardScale = 0.42f;
    private const float EllipseLift = 26f;
    private const float Deg = MathF.PI / 180f;

    private static readonly float[] RingScales = { 1f, 0.82f, 0.7f, 0.6f };
    private static readonly float[] CenterScales = { 1f, 0.92f, 0.84f, 0.8f };
    private static readonly float[][] BaseRx =
    {
        new[] { 620f },
        new[] { 560f, 700f },
        new[] { 520f, 620f, 710f },
        new[] { 500f, 580f, 650f, 720f },
    };
    private static readonly float[][] BaseRy =
    {
        new[] { 290f },
        new[] { 262f, 318f },
        new[] { 240f, 280f, 320f },
        new[] { 232f, 262f, 292f, 322f },
    };

    private readonly float[] _rx = new float[4];
    private readonly float[] _ry = new float[4];
    private readonly List<PathPoint> _path = new();
    private (int N, int Rings, float Shape, float Spacing, float Card, float Expansion) _cacheKey = (-1, 0, 0, 0, 0, 0);
    private int _rings = 1;
    private float _centerScale = 1f;

    private readonly record struct PathPoint(double S, float Theta, float Ring);

    public SolarSystemLayout(bool minimal = false)
    {
        Mode = minimal ? SwitcherMode.OrbitMinimal : SwitcherMode.SolarSystem;
    }

    public SwitcherMode Mode { get; }

    public static int AutoRingCount(int windowCount)
    {
        int others = windowCount - 1;
        return others <= 8 ? 1 : others <= 16 ? 2 : others <= 26 ? 3 : 4;
    }

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var solar = ctx.Solar;
        var motion = ctx.Motion;
        float t = ctx.Time;
        float idle = motion.IdleAmount;
        float mirror = solar.Direction == RotationDirection.Clockwise ? 1f : -1f;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;

        Configure(n, solar, ctx.CardSize, ctx.Expansion);
        int rings = _rings;
        float cardBox = ctx.CardSize * _centerScale;

        // ── Geometry in design units ────────────────────────────────────────────
        var centerD = new Vector2(DesignSpace.Width * 0.5f, CenterY);
        var ellipseCenterD = centerD + new Vector2(0f, -EllipseLift) - parallax * 6f;
        float roll = LayoutMath.Deg(-2.5f + parallax.X * 1.5f);
        var ellipseCenterPx = sp.ToPx(ellipseCenterD);
        for (int r = 0; r < rings; r++)
        {
            result.Rings.Add(new OrbitRing(ellipseCenterPx, new Vector2(_rx[r], _ry[r]) * sp.Scale, roll,
                r == 0 ? 1f : 0.78f - 0.1f * r, r));
        }

        var centerPx = sp.ToPx(centerD - parallax * 12f);
        float breathing = 1f + motion.BreathingAmplitude * MathF.Sin(t * MathF.Tau / motion.BreathingPeriod);
        float rotation = solar.RotationIntensity / 0.6f * motion.RotationIntensity;
        float maxYaw = LayoutMath.Deg(12f) * (0.4f + 1.2f * solar.Perspective) * motion.DepthIntensity;
        float backScale = Easing.Lerp(0.86f, 0.52f, solar.CardDepth);
        // Crowded systems use smaller planets so neighbours on the same orbit keep their distance.
        float density = Math.Clamp(1.14f - 0.016f * n, 0.72f, 1f);

        result.Anchor = centerPx;
        result.FocusBottom = centerPx.Y + sp.Px(CenterBoxHeight * cardBox * 0.5f + CardPadding + InfoStripHeight);

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            Vector2 box = LayoutMath.Fit(items[i].Aspect, CenterBoxWidth * cardBox, CenterBoxHeight * cardBox);

            if (n == 1)
            {
                pose = CenterPose(centerPx, box * sp.Scale, breathing, cardBox);
                continue;
            }

            double d = OrbitalRotor.WrapOffset(i - ctx.RotorPosition, n);
            // With two windows both neighbours are the same item: keep it on one side so the swap
            // stays continuous instead of mirroring through the back of the orbit.
            float side = d >= 0 || n == 2 ? 1f : -1f;
            float s = (float)Math.Abs(d);

            Evaluate(s, out float theta, out float ringF);
            float rx = Sample(_rx, ringF), ry = Sample(_ry, ringF), ringScale = Sample(RingScales, ringF);

            float swing = (float)Math.Clamp(ctx.RotorVelocity * 0.022 * (1 + 0.35 * ringF) * rotation, -0.3, 0.3);
            int ringIndex = Math.Clamp((int)MathF.Round(ringF), 0, 3);
            float sway = LayoutMath.Deg(2.2f) * idle * MathF.Sin(t * MathF.Tau / 22f + ringIndex * 1.3f) * (ringIndex % 2 == 0 ? 1f : -1f);
            float phi = side * theta + swing + sway;

            var local = new Vector2(mirror * rx * MathF.Sin(phi), ry * MathF.Cos(phi));
            local = LayoutMath.Rotate(local, roll);
            float depthFront = (1f + MathF.Cos(phi)) * 0.5f;

            // The first slot on either side always stays crisp and readable.
            float prominence = 1f - Math.Clamp(s - 1f, 0f, 1f);
            float df = Easing.Lerp(depthFront, 1f, prominence * 0.6f);
            float depthScale = Easing.Lerp(1f, Easing.Lerp(backScale, 1f, df), motion.DepthIntensity);

            float floatY = MathF.Sin(t * MathF.Tau / 7f + i * 1.93f) * 2.4f * idle;
            var orbitD = ellipseCenterD + local + new Vector2(0f, floatY) - parallax * (6f + 8f * depthFront);
            var orbitPx = sp.ToPx(orbitD);

            float orbitScale = OrbitCardScale * density * ringScale * depthScale;
            float c = s < 1f ? Easing.Smootherstep(1f - s) : 0f;
            float flight = MathF.Sin(MathF.PI * c);
            float rel = Easing.Lerp(orbitScale, breathing, c) * (1f + 0.05f * flight * motion.ScaleIntensity);

            pose.Center = Vector2.Lerp(orbitPx, centerPx, c);
            pose.PreviewSize = box * sp.Scale * rel;
            pose.Scale = rel * cardBox;
            pose.Opacity = Easing.Lerp(Easing.Lerp(0.78f, 1f, df), 1f, c);
            pose.Brightness = Easing.Lerp(Easing.Lerp(0.5f, 1f, df), 1f, c);
            pose.Blur = Easing.Lerp(Math.Clamp((1f - df) * 0.85f * motion.DepthIntensity + ringF * 0.12f, 0f, 1f), 0f, c);
            pose.Yaw = Easing.Lerp(-mirror * MathF.Sin(phi) * maxYaw, 0f, c);
            pose.Pitch = 0f;
            pose.Z = 0f;
            pose.Depth = Easing.Lerp(1f - depthFront, 0f, c);
            pose.Focus = c;
            pose.Glow = Easing.Lerp(0.28f + 0.22f * df, 1f, c);
            pose.InfoAlpha = Easing.Smoothstep(0.55f, 0.97f, c);
            pose.LabelAlpha = ctx.Expansion * (1f - Easing.Smoothstep(0.05f, 0.45f, c)) * Easing.Lerp(0.5f, 1f, df);
            pose.Reflection = 0f;
            pose.SortKey = depthFront - ringF * 0.01f + c * 10f;
        }
    }

    private static CardPose CenterPose(Vector2 center, Vector2 size, float breathing, float cardBox) => new()
    {
        Center = center,
        PreviewSize = size * breathing,
        Scale = breathing * cardBox,
        Opacity = 1f,
        Brightness = 1f,
        Focus = 1f,
        Glow = 1f,
        InfoAlpha = 1f,
        SortKey = 11f,
    };

    private static float Sample(float[] values, float index)
    {
        int a = Math.Clamp((int)MathF.Floor(index), 0, values.Length - 1);
        int b = Math.Min(a + 1, values.Length - 1);
        return Easing.Lerp(values[a], values[b], Math.Clamp(index - a, 0f, 1f));
    }

    /// <summary>Chooses orbit count and radii, then builds the per-side path through the resting slots.</summary>
    private void Configure(int n, SolarSystemSettings solar, float cardSize, float expansion)
    {
        int requested = Math.Clamp(solar.OrbitCount > 0 ? solar.OrbitCount : AutoRingCount(n), 1, 4);
        var key = (n, requested, solar.OrbitShape, solar.OrbitSpacing, cardSize, MathF.Round(expansion * 100f) / 100f);
        if (key == _cacheKey) return;
        _cacheKey = key;

        double half = n / 2.0;
        double perSide = (n - 1) / 2.0;
        int rings = requested;
        var capacities = new int[4];
        for (; rings > 1; rings--)
        {
            // Split slots between orbits in proportion to how many cards each can hold.
            double weightSum = 0;
            for (int r = 0; r < rings; r++) weightSum += BaseRx[rings - 1][r] / RingScales[r];
            double used = 0.5;
            for (int r = 0; r < rings - 1; r++)
            {
                double ideal = perSide * (BaseRx[rings - 1][r] / RingScales[r]) / weightSum;
                capacities[r] = Math.Max(r == 0 ? 2 : 1, (int)Math.Round(ideal));
                used += capacities[r];
            }
            if (half - used >= 1.5) break;
        }

        _rings = rings;
        _centerScale = CenterScales[rings - 1];
        float grow = 1f + 0.04f * expansion;
        float shape = solar.OrbitShape;
        float spacing = Easing.Lerp(0.8f, 1.1f, solar.OrbitSpacing);
        float cardShift = (cardSize - 1f) * 250f * _centerScale;
        for (int r = 0; r < rings; r++)
        {
            float rx = BaseRx[rings - 1][0] + (BaseRx[rings - 1][r] - BaseRx[rings - 1][0]) * spacing + cardShift;
            float ry = BaseRy[rings - 1][0] + (BaseRy[rings - 1][r] - BaseRy[rings - 1][0]) * spacing + cardShift * 0.45f;
            // Two or three windows would look lost on a full-size orbit.
            float sparse = rings == 1 ? Easing.Lerp(0.82f, 1f, Math.Clamp((n - 3) / 4f, 0f, 1f)) : 1f;
            _rx[r] = MathF.Min(735f, rx * Easing.Lerp(0.95f, 1.05f, shape) * sparse) * grow;
            _ry[r] = ry * Easing.Lerp(1.1f, 0.9f, shape) * grow;
        }

        BuildPath(n, rings, capacities, half);
    }

    private void BuildPath(int n, int rings, int[] capacities, double half)
    {
        _path.Clear();
        _path.Add(new PathPoint(0, 0f, 0f));

        if (n == 2)
        {
            _path.Add(new PathPoint(1, 78f * Deg, 0f));
            return;
        }

        double start = 0;
        float previousLast = 0f;
        for (int r = 0; r < rings; r++)
        {
            bool last = r == rings - 1;
            double end = last ? half : (r == 0 ? capacities[0] + 0.5 : start + capacities[r]);

            var slots = new List<double>();
            for (double j = Math.Floor(start) + 1; j <= end + 1e-9; j++)
                if (j > start + 1e-9) slots.Add(j);

            bool meeting = last && slots.Count > 0 && Math.Abs(slots[^1] - end) < 1e-9;
            int spread = slots.Count - (meeting ? 1 : 0);
            float[] angles = SlotAngles(r, spread);
            bool ascending = r % 2 == 0;

            if (r > 0 && spread > 0)
            {
                // Orbit transfer: halfway between the last slot of the inner orbit and the first of this one.
                float first = (ascending ? angles[0] : angles[^1]) * Deg;
                _path.Add(new PathPoint(start, (previousLast + first) * 0.5f, r - 0.5f));
            }

            for (int k = 0; k < spread; k++)
            {
                float a = (ascending ? angles[k] : angles[spread - 1 - k]) * Deg;
                _path.Add(new PathPoint(slots[k], a, r));
                previousLast = a;
            }

            if (last)
            {
                // Both sides meet here, so the wrap from the last window to the first is seamless.
                _path.Add(new PathPoint(half, ascending ? MathF.PI : 0f, r));
            }
            start = end;
        }
    }

    /// <summary>Resting angles (degrees from the front) for the slots of one orbit, front to back.</summary>
    private static float[] SlotAngles(int ring, int count)
    {
        if (count <= 0) return Array.Empty<float>();
        if (ring == 0)
        {
            if (count == 1) return new[] { 78f };
            if (count == 2) return new[] { 62f, 128f };
            return Linspace(54f, 146f, count);
        }
        if (count == 1) return new[] { 100f };
        return Linspace(34f, 160f, count);
    }

    private static float[] Linspace(float from, float to, int count)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = from + (to - from) * i / (count - 1);
        return a;
    }

    private void Evaluate(float s, out float theta, out float ring)
    {
        var path = _path;
        if (s <= path[0].S) { theta = path[0].Theta; ring = path[0].Ring; return; }
        for (int i = 1; i < path.Count; i++)
        {
            if (s <= path[i].S)
            {
                var a = path[i - 1];
                var b = path[i];
                float u = (float)((s - a.S) / Math.Max(1e-6, b.S - a.S));
                theta = Easing.Lerp(a.Theta, b.Theta, u);
                ring = Easing.Lerp(a.Ring, b.Ring, u);
                return;
            }
        }
        theta = path[^1].Theta;
        ring = path[^1].Ring;
    }
}
