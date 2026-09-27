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
/// <para>
/// Card sizes come from Settings (selected and orbit size separately). The orbits are then solved
/// for the monitor at hand (see <see cref="Configure"/>): they widen, concentrate the planets on
/// the flanks, gain an orbit or — as a last resort — shrink the cards until no planet covers the
/// selected card, planets keep apart and everything stays on screen.
/// </para>
/// </remarks>
public sealed class SolarSystemLayout : ILayoutEngine
{
    public const float CenterBoxWidth = 500f;
    public const float CenterBoxHeight = 300f;
    public const float CenterY = 410f;
    public const float CardPadding = 12f;
    public const float InfoStripHeight = 64f;

    /// <summary>Cards behind the selected card may tuck this far (design units) under its edge — a depth cue.</summary>
    public const float BackTuck = 40f;

    /// <summary>Overlap (design units) that still counts as touching rather than covering.</summary>
    public const float Tolerance = 2f;

    /// <summary>Room (design units) kept for the name label under or above a planet in the expanded stage.</summary>
    public const float LabelSpace = 24f;

    /// <summary>Clear space (design units) between the selected card and planets in front of it at custom card sizes.</summary>
    public const float Clearance = 10f;

    /// <summary>Kept free at the bottom of the monitor in the expanded stage, for the desktop strip and key hints.</summary>
    public const float BottomChrome = 62f;

    /// <summary>Share of a planet's name label another planet may cover.</summary>
    private const float LabelCoverLimit = 0.1f;

    private const float OrbitCardScale = 0.42f;
    private const float EllipseLift = 26f;
    private const float Deg = MathF.PI / 180f;
    private const float MaxGrowth = 3f;
    private const float MinShrinkOrbit = 0.5f;
    private const float MinShrinkCenter = 0.6f;
    /// <summary>Orbits may become up to this much taller relative to their width.</summary>
    private const float MaxStretch = 0.5f;

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

    private static readonly DesignSpace Neutral = new(new Vector2(DesignSpace.Width, DesignSpace.Height), 1f, Vector2.Zero);

    private readonly float[] _rx = new float[4];
    private readonly float[] _ry = new float[4];
    private readonly int[] _capacities = new int[4];
    private readonly List<PathPoint> _path = new();
    private readonly List<Probe> _probes = new();
    private ConfigKey _cacheKey;
    private bool _configured;
    private Candidate _solution;
    private float _appliedExpansion = float.NaN;
    private int _topology;
    /// <summary>Label room for the configuration being solved (labels show in the expanded stage only).</summary>
    private float _labelSpace;
    /// <summary>Stage (0 = compact, 1 = expanded) of the arrangement being solved.</summary>
    private float _solveExpansion;
    private bool _protectLabels;
    private int _rings = 1;
    private float _centerScale = 1f;
    /// <summary>Scale of the selected card (1 = 500 × 300 design units).</summary>
    private float _centerK = 1f;
    /// <summary>Multiplier on the designed planet size.</summary>
    private float _orbitK = 1f;

    private readonly record struct PathPoint(double S, float Theta, float Ring);

    private readonly record struct ConfigKey(int N, int Requested, float Shape, float Spacing, float CardDepth, float DepthIntensity,
        bool Mirror, float Selected, float Orbit, bool Expanded, Vector2 VisibleMin, Vector2 VisibleMax);

    /// <summary>A planet at rest, in design units relative to the orbit centre, sampled at radius scale 1.</summary>
    private readonly record struct Probe(Vector2 Local, Vector2 Half, bool Back)
    {
        /// <summary>Name label (expanded stage) relative to the planet's centre: under planets in front, above planets behind.</summary>
        public (Vector2 Offset, Vector2 Half) Label(float space)
        {
            var half = new Vector2(MathF.Max(55f, Half.X + 10f), space * 0.5f * 0.75f);
            float y = Half.Y + space * 0.25f + half.Y;
            return (new Vector2(0f, Back ? -y : y), half);
        }
    }

    /// <summary>One way of arranging the system; the solver searches over these.</summary>
    private struct Candidate
    {
        public int Rings;
        /// <summary>0 = designed slot angles, 1 = slots gathered on the flanks, away from the centre card.</summary>
        public float Arc;
        /// <summary>0 = requested card sizes, 1 = cards shrunk as far as the solver may.</summary>
        public float Shrink;
        /// <summary>0 = designed ellipses, 1 = orbits <see cref="MaxStretch"/> taller (lifts planets over and under a large centre card).</summary>
        public float Stretch;
        /// <summary>Uniform scale of every orbit radius (1 = the designed radii for these card sizes).</summary>
        public float Growth;
    }

    /// <summary>Per-frame values shared by every card.</summary>
    private struct Frame
    {
        public DesignSpace Space;
        public Vector2 EllipseCenter;
        public Vector2 CenterPx;
        public Vector2 Parallax;
        public float Mirror, Roll, Breathing, Rotation, MaxYaw, BackScale, Density, Idle, Time, Expansion;
        public float ScaleIntensity, DepthIntensity;
        public double Velocity;
        public CardSizing Size;
    }

    public SolarSystemLayout(bool minimal = false)
    {
        Mode = minimal ? SwitcherMode.OrbitMinimal : SwitcherMode.SolarSystem;
    }

    public SwitcherMode Mode { get; }

    public bool Wraps => true;

    /// <summary>Orbits used by the last <see cref="Compute"/>.</summary>
    public int RingCount => _rings;

    /// <summary>How the last configuration was relaxed from the designed geometry (for tests and diagnostics).</summary>
    internal (float Arc, float Stretch, float Shrink, float Growth) Relaxation { get; private set; }

    /// <summary>Fewest orbits that hold <paramref name="windowCount"/> windows without piling them up.</summary>
    public static int MinimumRingCount(int windowCount) => windowCount <= 10 ? 1 : windowCount <= 26 ? 2 : 3;

    /// <summary>Up to 5 windows: one orbit; 6–10: two; beyond that three, then four.</summary>
    public static int AutoRingCount(int windowCount) =>
        windowCount <= 5 ? 1 : windowCount <= 10 ? 2 : windowCount <= 18 ? 3 : 4;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        var sp = ctx.Space;
        var motion = ctx.Motion;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;

        Configure(n, ctx);
        var f = CreateFrame(ctx, n, sp, parallax, ctx.Time, motion.IdleAmount, ctx.RotorVelocity,
            1f + motion.BreathingAmplitude * MathF.Sin(ctx.Time * MathF.Tau / motion.BreathingPeriod));

        var ellipseCenterPx = sp.ToPx(f.EllipseCenter);
        for (int r = 0; r < _rings; r++)
        {
            result.Rings.Add(new OrbitRing(ellipseCenterPx, new Vector2(_rx[r], _ry[r]) * sp.Scale, f.Roll,
                r == 0 ? 1f : 0.78f - 0.1f * r, r));
        }

        result.Anchor = f.CenterPx;
        result.FocusBottom = f.CenterPx.Y + sp.Px(CenterBottom(ctx.CardSize) - CenterY);
        result.Topology = _topology;

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            if (n == 1)
            {
                Vector2 box = LayoutMath.Fit(items[i].Aspect, CenterBoxWidth, CenterBoxHeight);
                pose = CenterPose(f.CenterPx, box * sp.Scale * _centerK, f.Breathing, _centerK,
                    CardSizing.TypeFactor(ctx.CardSize.Selected));
                continue;
            }

            double d = OrbitalRotor.WrapOffset(i - ctx.RotorPosition, n);
            // With two windows both neighbours are the same item: keep it on one side so the swap
            // stays continuous instead of mirroring through the back of the orbit.
            float side = d >= 0 || n == 2 ? 1f : -1f;
            Place(ref pose, f, (float)Math.Abs(d), side, items[i].Aspect, i);
        }
    }

    private Frame CreateFrame(LayoutContext ctx, int n, DesignSpace sp, Vector2 parallax, float time, float idle, double velocity, float breathing)
    {
        var solar = ctx.Solar;
        var motion = ctx.Motion;
        var centerD = new Vector2(DesignSpace.Width * 0.5f, CenterY);
        return new Frame
        {
            Space = sp,
            EllipseCenter = centerD + new Vector2(0f, -EllipseLift) - parallax * 6f,
            CenterPx = sp.ToPx(centerD - parallax * 12f),
            Parallax = parallax,
            Mirror = solar.Direction == RotationDirection.Clockwise ? 1f : -1f,
            Roll = LayoutMath.Deg(-2.5f + parallax.X * 1.5f),
            Breathing = breathing,
            Rotation = solar.RotationIntensity / 0.6f * motion.RotationIntensity,
            MaxYaw = LayoutMath.Deg(12f) * (0.4f + 1.2f * solar.Perspective) * motion.DepthIntensity,
            BackScale = Easing.Lerp(0.86f, 0.52f, solar.CardDepth),
            // Crowded systems use smaller planets so neighbours on the same orbit keep their distance.
            Density = Math.Clamp(1.14f - 0.016f * n, 0.72f, 1f),
            Idle = idle,
            Time = time,
            Expansion = ctx.Expansion,
            ScaleIntensity = motion.ScaleIntensity,
            DepthIntensity = motion.DepthIntensity,
            Velocity = velocity,
            Size = ctx.CardSize,
        };
    }

    /// <summary>Pose of the card at path distance <paramref name="s"/> (0 = selected) on one side.</summary>
    private void Place(ref CardPose pose, in Frame f, float s, float side, float aspect, int index)
    {
        var sp = f.Space;
        Vector2 box = LayoutMath.Fit(aspect, CenterBoxWidth, CenterBoxHeight);

        Evaluate(s, out float theta, out float ringF);
        float rx = Sample(_rx, ringF), ry = Sample(_ry, ringF), ringScale = Sample(RingScales, ringF);

        float swing = (float)Math.Clamp(f.Velocity * 0.022 * (1 + 0.35 * ringF) * f.Rotation, -0.3, 0.3);
        int ringIndex = Math.Clamp((int)MathF.Round(ringF), 0, 3);
        float sway = LayoutMath.Deg(2.2f) * f.Idle * MathF.Sin(f.Time * MathF.Tau / 22f + ringIndex * 1.3f) * (ringIndex % 2 == 0 ? 1f : -1f);
        float phi = side * theta + swing + sway;

        var local = new Vector2(f.Mirror * rx * MathF.Sin(phi), ry * MathF.Cos(phi));
        local = LayoutMath.Rotate(local, f.Roll);
        float depthFront = (1f + MathF.Cos(phi)) * 0.5f;

        // The first slot on either side always stays crisp and readable.
        float prominence = 1f - Math.Clamp(s - 1f, 0f, 1f);
        float df = Easing.Lerp(depthFront, 1f, prominence * 0.6f);
        float depthScale = Easing.Lerp(1f, Easing.Lerp(f.BackScale, 1f, df), f.DepthIntensity);

        float floatY = MathF.Sin(f.Time * MathF.Tau / 7f + index * 1.93f) * 2.4f * f.Idle;
        var orbitD = f.EllipseCenter + local + new Vector2(0f, floatY) - f.Parallax * (6f + 8f * depthFront);
        var orbitPx = sp.ToPx(orbitD);

        float orbitScale = OrbitCardScale * f.Density * ringScale * depthScale * _orbitK;
        float c = s < 1f ? Easing.Smootherstep(1f - s) : 0f;
        float flight = MathF.Sin(MathF.PI * c);
        float rel = Easing.Lerp(orbitScale, f.Breathing * _centerK, c) * (1f + 0.05f * flight * f.ScaleIntensity);

        pose.Center = Vector2.Lerp(orbitPx, f.CenterPx, c);
        pose.PreviewSize = box * sp.Scale * rel;
        pose.Scale = rel;
        pose.TypeScale = rel * CardSizing.TypeFactor(f.Size.At(c));
        pose.Opacity = Easing.Lerp(Easing.Lerp(0.78f, 1f, df), 1f, c);
        pose.Brightness = Easing.Lerp(Easing.Lerp(0.5f, 1f, df), 1f, c);
        pose.Blur = Easing.Lerp(Math.Clamp((1f - df) * 0.85f * f.DepthIntensity + ringF * 0.12f, 0f, 1f), 0f, c);
        pose.Yaw = Easing.Lerp(-f.Mirror * MathF.Sin(phi) * f.MaxYaw, 0f, c);
        pose.Pitch = 0f;
        pose.Z = 0f;
        pose.Depth = Easing.Lerp(1f - depthFront, 0f, c);
        pose.Focus = c;
        pose.Glow = Easing.Lerp(0.28f + 0.22f * df, 1f, c);
        pose.InfoAlpha = Easing.Smoothstep(0.55f, 0.97f, c);
        pose.LabelAlpha = f.Expansion * (1f - Easing.Smoothstep(0.05f, 0.45f, c)) * Easing.Lerp(0.5f, 1f, df);
        pose.Reflection = 0f;
        pose.SortKey = depthFront - ringF * 0.01f + c * 10f;
    }

    private static CardPose CenterPose(Vector2 center, Vector2 size, float breathing, float scale, float typeFactor) => new()
    {
        Center = center,
        PreviewSize = size * breathing,
        Scale = breathing * scale,
        TypeScale = breathing * scale * typeFactor,
        Opacity = 1f,
        Brightness = 1f,
        Focus = 1f,
        Glow = 1f,
        InfoAlpha = 1f,
        SortKey = 11f,
    };

    /// <summary>Bottom edge (design units) of the selected card including its info strip.</summary>
    private float CenterBottom(CardSizing size) =>
        CenterY + (CenterBoxHeight * 0.5f + CardPadding) * _centerK + InfoStripHeight * _centerK * CardSizing.TypeFactor(size.Selected);

    private static float Sample(float[] values, float index)
    {
        int a = Math.Clamp((int)MathF.Floor(index), 0, values.Length - 1);
        int b = Math.Min(a + 1, values.Length - 1);
        return Easing.Lerp(values[a], values[b], Math.Clamp(index - a, 0f, 1f));
    }

    // ───────────────────────────── solving the system ─────────────────────────────

    /// <summary>
    /// Chooses orbit count, radii, slot angles and card scale for the window count, card sizes and
    /// monitor, then builds the per-side path through the resting slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The designed geometry (radii grown by what the card sizes add) is used whenever it is clean;
    /// at the default size it always is, so the default layout is exactly the designed one. If it
    /// is not — the selected card is very large, planets are big or the monitor is narrow — the
    /// solver relaxes it, each step only as far as needed:
    /// </para>
    /// <list type="number">
    /// <item>widen every orbit (up to the edge of the monitor, ultrawide space included);</item>
    /// <item>gather the planets toward the flanks of the orbits, away from the selected card;</item>
    /// <item>make the orbits taller, lifting planets over and under the selected card;</item>
    /// <item>use another orbit (more are preferred to fewer);</item>
    /// <item>shrink the planets, and only then the selected card.</item>
    /// </list>
    /// <para>
    /// A candidate is clean when, with every card at rest: no planet in front touches the selected
    /// card or its info strip, planets behind it tuck under its edge by at most
    /// <see cref="BackTuck"/>, planets do not pile up on each other or cover each other's names, and
    /// every card and its name stays on screen, clear of the desktop strip. The conditions are linear
    /// in the orbit radius and are solved in closed form, so a solve takes well under a millisecond.
    /// It runs only when an input changes; the compact and the expanded stage are solved separately.
    /// </para>
    /// </remarks>
    private void Configure(int n, LayoutContext ctx)
    {
        var solar = ctx.Solar;
        var size = ctx.CardSize;
        // A chosen orbit count is honoured as long as the orbits can hold the windows cleanly.
        int requested = Math.Clamp(solar.OrbitCount > 0 ? Math.Max(solar.OrbitCount, MinimumRingCount(n)) : AutoRingCount(n), 1, 4);
        // The expanded stage (names under planets, desktop strip) is solved as its own arrangement;
        // the animator moves the cards between the two when the stage changes.
        bool expanded = ctx.Expansion >= 0.5f;
        var key = new ConfigKey(n, requested, solar.OrbitShape, solar.OrbitSpacing, solar.CardDepth, ctx.Motion.DepthIntensity,
            solar.Direction == RotationDirection.Clockwise, size.Selected, size.Orbit, expanded,
            Round(ctx.Space.VisibleMin), Round(ctx.Space.VisibleMax));
        if (!_configured || key != _cacheKey)
        {
            _cacheKey = key;
            _configured = true;
            _solution = Solve(n, requested, ctx, expanded ? 1f : 0f);
            _topology = HashCode.Combine(_rings, Quantize(_solution.Arc), Quantize(_solution.Stretch), Quantize(_solution.Shrink), Quantize(_solution.Growth));
            Relaxation = (_solution.Arc, _solution.Stretch, _solution.Shrink, _solution.Growth);
            _appliedExpansion = float.NaN;
        }

        // The designed 4 % growth of the expanded stage stays continuous.
        float expansion = ctx.Expansion;
        if (expansion == _appliedExpansion) return;
        _appliedExpansion = expansion;
        Apply(n, _solution, solar, size, expansion);
    }

    private static int Quantize(float v) => (int)MathF.Round(v * 40f);

    /// <summary>Solves the arrangement for one stage (0 = compact, 1 = expanded).</summary>
    private Candidate Solve(int n, int requested, LayoutContext ctx, float stage)
    {
        var solar = ctx.Solar;
        var size = ctx.CardSize;
        _labelSpace = LabelSpace * stage;
        _solveExpansion = stage;

        if (n <= 1)
        {
            // A lone window: only the selected card, shrunk if it would not fit the monitor
            // (shrink values above 0.5 shrink the selected card, see Apply).
            var screen = (ctx.Space.VisibleMin, ctx.Space.VisibleMax);
            for (int step = 0; step <= 10; step++)
            {
                float shrink = step == 0 ? 0f : 0.5f + step * 0.05f;
                if (CenterFits(screen, size, 1, shrink)) return new Candidate { Rings = 1, Growth = 1f, Shrink = shrink };
            }
            return new Candidate { Rings = 1, Growth = 1f, Shrink = 1f };
        }

        // Neighbour overlap allowed at rest: the automatic layouts people see daily keep planets
        // apart; very full or hand-picked systems may stack slightly in depth, never hide one another.
        float overlapLimit = solar.OrbitCount == 0 && n <= 16 ? 0.05f : 0.25f;
        // Where planets are kept apart they keep clear of each other's names too; fuller or
        // hand-picked systems may stack names slightly, as they stack planets.
        _protectLabels = overlapLimit <= 0.05f;
        // In the expanded stage the desktop strip and key hints take the bottom of the monitor.
        var bounds = (Min: ctx.Space.VisibleMin, Max: ctx.Space.VisibleMax - new Vector2(0f, BottomChrome * stage));

        Candidate best = default;
        bool found = TrySolve(n, EffectiveRings(n, requested), 0f, ctx, bounds, overlapLimit, out best);
        if (!found)
        {
            // The designed orbit count does not fit these card sizes. Score every orbit count by how
            // much it must shrink the cards and how far it strays from the designed count: another
            // orbit is cheap, fewer orbits cost more, shrinking costs most.
            float bestScore = float.MaxValue;
            Span<int> seen = stackalloc int[4];
            int tried = 0;
            ReadOnlySpan<int> offsets = stackalloc int[] { 0, 1, 2, 3, -1, -2, -3 };
            foreach (int offset in offsets)
            {
                int want = requested + offset;
                if (want is < 1 or > 4) continue;
                int rings = EffectiveRings(n, want);
                if (seen[..tried].Contains(rings)) continue;
                seen[tried++] = rings;
                int distance = rings - EffectiveRings(n, requested);
                float penalty = distance > 0 ? 0.05f * distance : 0.2f * -distance;
                if (penalty >= bestScore || !TrySolve(n, rings, 1f, ctx, bounds, overlapLimit, out var solution)) continue;
                float shrink = 1f;
                if (TrySolve(n, rings, 0f, ctx, bounds, overlapLimit, out var clean))
                {
                    shrink = 0f;
                    solution = clean;
                }
                else
                {
                    float lo = 0f, hi = 1f;
                    for (int it = 0; it < 8 && penalty + lo < bestScore; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (TrySolve(n, rings, mid, ctx, bounds, overlapLimit, out var c)) { hi = mid; solution = c; }
                        else lo = mid;
                    }
                    shrink = hi;
                }
                if (penalty + shrink < bestScore)
                {
                    bestScore = penalty + shrink;
                    best = solution;
                    found = true;
                }
            }
        }

        if (!found)
        {
            // Pathological input (a tiny monitor, dozens of windows): the tightest arrangement we have.
            best = new Candidate { Rings = EffectiveRings(n, requested), Arc = 1f, Shrink = 1f, Growth = 1f };
            Apply(n, best, solar, size, stage);
            Sample(n, ctx);
            best.Growth = Math.Max(0.3f, MaxRadiusScale(bounds));
        }
        return best;
    }

    private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));

    /// <summary>
    /// Finds the gentlest arrangement for a fixed orbit count and shrink, making the orbits taller
    /// only if the designed ellipse cannot hold the planets.
    /// </summary>
    private bool TrySolve(int n, int rings, float shrink, LayoutContext ctx, (Vector2 Min, Vector2 Max) bounds, float overlapLimit,
        out Candidate result)
    {
        result = new Candidate { Rings = rings, Shrink = shrink, Growth = 1f };
        if (!CenterFits(bounds, ctx.CardSize, rings, shrink)) return false;
        if (TryArrange(n, rings, shrink, 0f, ctx, bounds, overlapLimit, out result)) return true;
        if (!TryArrange(n, rings, shrink, 1f, ctx, bounds, overlapLimit, out result)) return false;
        float lo = 0f, hi = 1f;
        for (int it = 0; it < 7; it++)
        {
            float mid = (lo + hi) * 0.5f;
            if (TryArrange(n, rings, shrink, mid, ctx, bounds, overlapLimit, out var c)) { hi = mid; result = c; }
            else lo = mid;
        }
        return true;
    }

    /// <summary>
    /// For fixed orbit count, shrink and stretch: the least flank gathering that clears the selected
    /// card, then the least widening that also keeps planets apart.
    /// </summary>
    private bool TryArrange(int n, int rings, float shrink, float stretch, LayoutContext ctx, (Vector2 Min, Vector2 Max) bounds,
        float overlapLimit, out Candidate result)
    {
        var solar = ctx.Solar;
        var size = ctx.CardSize;
        result = new Candidate { Rings = rings, Shrink = shrink, Stretch = stretch, Growth = 1f };

        // Least flank gathering (Arc) for which some radius clears the selected card on screen.
        float Need(float arc, out float lo, out float hi)
        {
            var c = new Candidate { Rings = rings, Shrink = shrink, Stretch = stretch, Arc = arc, Growth = 1f };
            Apply(n, c, solar, size, _solveExpansion);
            Sample(n, ctx);
            hi = MaxRadiusScale(bounds);
            lo = MinRadiusScaleForCenter(size);
            return hi - lo;
        }

        float arc = 0f;
        float slack = Need(0f, out float gLo, out float gHi);
        if (slack < 0f)
        {
            if (Need(1f, out _, out _) < 0f) return false;
            float a = 0f, b = 1f;
            for (int it = 0; it < 9; it++)
            {
                float mid = (a + b) * 0.5f;
                if (Need(mid, out _, out _) >= 0f) b = mid;
                else a = mid;
            }
            arc = b;
            Need(arc, out gLo, out gHi);
        }

        // The designed radii when they are clean; otherwise the least widening that is.
        float growth = MathF.Max(MathF.Min(1f, gHi), gLo);
        growth = MinRadiusScaleForNeighbours(growth, gHi, overlapLimit);
        if (growth > gHi + 1e-4f) return false;

        result.Arc = arc;
        result.Growth = growth;
        return true;
    }

    /// <summary>Orbit count the window count actually supports when <paramref name="requested"/> are asked for.</summary>
    private int EffectiveRings(int n, int requested)
    {
        ComputeCapacities(n, requested, _capacities, out int rings);
        return rings;
    }

    private static void ComputeCapacities(int n, int requested, int[] capacities, out int rings)
    {
        double half = n / 2.0;
        double perSide = (n - 1) / 2.0;
        Array.Clear(capacities);
        for (rings = requested; rings > 1; rings--)
        {
            // Split slots between orbits in proportion to how many cards each can hold.
            double weightSum = 0;
            for (int r = 0; r < rings; r++) weightSum += BaseRx[rings - 1][r] / RingScales[r];
            double used = 0.5;
            for (int r = 0; r < rings - 1; r++)
            {
                double ideal = perSide * (BaseRx[rings - 1][r] / RingScales[r]) / weightSum;
                capacities[r] = Math.Max(1, (int)Math.Round(ideal));
                used += capacities[r];
            }
            if (half - used >= 1.5) break;
        }
    }

    /// <summary>Sets radii, card scales and the path for a candidate.</summary>
    private void Apply(int n, in Candidate c, SolarSystemSettings solar, CardSizing size, float expansion)
    {
        ComputeCapacities(n, c.Rings, _capacities, out int rings);
        _rings = rings;
        _centerScale = CenterScales[rings - 1];
        float orbitShrink = Easing.Lerp(1f, MinShrinkOrbit, Math.Clamp(c.Shrink * 2f, 0f, 1f));
        float centerShrink = Easing.Lerp(1f, MinShrinkCenter, Math.Clamp(c.Shrink * 2f - 1f, 0f, 1f));
        _centerK = size.Selected * _centerScale * centerShrink;
        _orbitK = size.Orbit * _centerScale * orbitShrink;

        float grow = 1f + 0.04f * expansion;
        float shape = solar.OrbitShape;
        float spacing = Easing.Lerp(0.8f, 1.1f, solar.OrbitSpacing);
        // Larger cards push the orbits out by what they add to the centre card and to the planets.
        float shiftX = ((_centerK / _centerScale - 1f) * 250f + (_orbitK / _centerScale - 1f) * 105f) * _centerScale;
        float shiftY = ((_centerK / _centerScale - 1f) * 112f + (_orbitK / _centerScale - 1f) * 55f) * _centerScale;
        for (int r = 0; r < rings; r++)
        {
            float rx = BaseRx[rings - 1][0] + (BaseRx[rings - 1][r] - BaseRx[rings - 1][0]) * spacing;
            float ry = BaseRy[rings - 1][0] + (BaseRy[rings - 1][r] - BaseRy[rings - 1][0]) * spacing;
            // Two or three windows would look lost on a full-size orbit.
            float sparse = rings == 1 ? Easing.Lerp(0.82f, 1f, Math.Clamp((n - 3) / 4f, 0f, 1f)) : 1f;
            // The size shift moves every orbit by the same amount, after the cap, so orbits keep their spacing.
            _rx[r] = (MathF.Min(735f, rx * Easing.Lerp(0.95f, 1.05f, shape) * sparse) + shiftX) * grow * c.Growth;
            _ry[r] = (ry * Easing.Lerp(1.1f, 0.9f, shape) + shiftY) * grow * c.Growth * (1f + MaxStretch * c.Stretch);
        }

        BuildPath(n, rings, _capacities, n / 2.0, c.Arc);
    }

    /// <summary>Records every planet at rest (rotor on a whole index, no idle motion) for the checks.</summary>
    private void Sample(int n, LayoutContext ctx)
    {
        _probes.Clear();
        var f = CreateFrame(ctx, n, Neutral, Vector2.Zero, 0f, 0f, 0, 1f);
        var pose = new CardPose();
        float aspect = CenterBoxWidth / CenterBoxHeight; // Fills the box: the largest card any window can get.
        for (int i = 1; i < n; i++)
        {
            double d = OrbitalRotor.WrapOffset(i, n);
            float side = d >= 0 || n == 2 ? 1f : -1f;
            Place(ref pose, f, (float)Math.Abs(d), side, aspect, i);
            var half = pose.PreviewSize * 0.5f + new Vector2(CardPadding * pose.Scale);
            _probes.Add(new Probe(pose.Center - f.EllipseCenter, half, pose.Depth >= 0.5f));
        }
    }

    private static Vector2 EllipseCenterDesign => new(DesignSpace.Width * 0.5f, CenterY - EllipseLift);

    /// <summary>Largest radius scale that keeps every planet and its label on the monitor.</summary>
    private float MaxRadiusScale((Vector2 Min, Vector2 Max) bounds)
    {
        var e = EllipseCenterDesign;
        float g = MaxGrowth;
        foreach (var p in _probes)
        {
            float above = p.Half.Y + (p.Back ? _labelSpace : 0f);
            float below = p.Half.Y + (p.Back ? 0f : _labelSpace);
            g = MathF.Min(g, Limit(p.Local.X, e.X - p.Half.X - bounds.Min.X, bounds.Max.X - p.Half.X - e.X));
            g = MathF.Min(g, Limit(p.Local.Y, e.Y - above - bounds.Min.Y, bounds.Max.Y - below - e.Y));
        }
        return g;

        // Largest g with  -room⁻ ≤ g·offset ≤ room⁺.
        static float Limit(float offset, float roomNeg, float roomPos)
        {
            if (offset > 1e-3f) return roomPos < 0 ? -1f : roomPos / offset;
            if (offset < -1e-3f) return roomNeg < 0 ? -1f : roomNeg / -offset;
            return roomNeg < 0 || roomPos < 0 ? -1f : MaxGrowth;
        }
    }

    /// <summary>Smallest radius scale at which no planet covers the selected card or its info strip.</summary>
    private float MinRadiusScaleForCenter(CardSizing size)
    {
        var e = EllipseCenterDesign;
        float halfW = (CenterBoxWidth * 0.5f + CardPadding) * _centerK;
        float top = CenterY - (CenterBoxHeight * 0.5f + CardPadding) * _centerK;
        float bottom = CenterBottom(size);
        float center = DesignSpace.Width * 0.5f;
        float g = 0f;
        // The designed geometry is kept verbatim at the designed size (planets may touch within the
        // tolerance); custom sizes get a little clear space, phased in so resizing stays smooth.
        float custom = MathF.Min(1f, 4f * (MathF.Abs(size.Selected - 1f) + MathF.Abs(size.Orbit - 1f)));
        float front = Easing.Lerp(-Tolerance, Clearance, custom);
        float back = Easing.Lerp(-Tolerance, 0f, custom) - BackTuck;
        foreach (var p in _probes)
        {
            // Planets behind the selected card may tuck under its edge; planets in front keep clear of it.
            float margin = p.Back ? back : front;
            float needX = halfW + margin + p.Half.X;
            float gx = MathF.Abs(p.Local.X) > 1e-3f ? MathF.Max(0f, needX - MathF.Abs(e.X - center)) / MathF.Abs(p.Local.X) : float.PositiveInfinity;
            float gy;
            if (p.Local.Y > 1e-3f) gy = MathF.Max(0f, bottom + margin + p.Half.Y - e.Y) / p.Local.Y;
            else if (p.Local.Y < -1e-3f) gy = MathF.Max(0f, e.Y + p.Half.Y - (top - margin)) / -p.Local.Y;
            else gy = float.PositiveInfinity;
            g = MathF.Max(g, MathF.Min(gx, gy));
        }
        return g;
    }

    /// <summary>
    /// Smallest radius scale ≥ <paramref name="from"/> at which no two planets pile up beyond the
    /// limit and no planet hides another one's name label.
    /// </summary>
    private float MinRadiusScaleForNeighbours(float from, float max, float limit)
    {
        float g = from;
        var probes = _probes;
        float labels = _protectLabels ? _labelSpace : 0f;
        for (int i = 0; i < probes.Count; i++)
        {
            for (int j = i + 1; j < probes.Count; j++)
            {
                var a = probes[i];
                var b = probes[j];
                if (Clear(a, b, g)) continue;
                if (!Clear(a, b, max)) return float.PositiveInfinity;
                float lo = g, hi = max;
                for (int it = 0; it < 14; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Clear(a, b, mid)) hi = mid;
                    else lo = mid;
                }
                g = hi;
            }
        }
        return g;

        bool Clear(in Probe a, in Probe b, float scale) =>
            OverlapRatio(a, b, scale) <= limit &&
            (labels <= 0f || (LabelCover(a, b, scale, labels) <= LabelCoverLimit && LabelCover(b, a, scale, labels) <= LabelCoverLimit));
    }

    /// <summary>Share of <paramref name="owner"/>'s name label covered by planet <paramref name="other"/>.</summary>
    private static float LabelCover(in Probe owner, in Probe other, float g, float space)
    {
        var (offset, half) = owner.Label(space);
        var delta = Vector2.Abs(owner.Local * g + offset - other.Local * g);
        float w = half.X + other.Half.X - delta.X;
        float h = half.Y + other.Half.Y - delta.Y;
        if (w <= 0f || h <= 0f) return 0f;
        return MathF.Min(w, 2f * half.X) * MathF.Min(h, 2f * half.Y) / (4f * half.X * half.Y);
    }

    private static float OverlapRatio(in Probe a, in Probe b, float g)
    {
        var delta = Vector2.Abs((a.Local - b.Local) * g);
        float w = a.Half.X + b.Half.X - delta.X;
        float h = a.Half.Y + b.Half.Y - delta.Y;
        if (w <= 0f || h <= 0f) return 0f;
        float area = 4f * MathF.Min(a.Half.X * a.Half.Y, b.Half.X * b.Half.Y);
        return w * h / MathF.Max(area, 1e-3f);
    }

    /// <summary>Whether the selected card itself fits on the monitor at this orbit count and shrink.</summary>
    private static bool CenterFits((Vector2 Min, Vector2 Max) bounds, CardSizing size, int rings, float shrink)
    {
        float centerShrink = Easing.Lerp(1f, MinShrinkCenter, Math.Clamp(shrink * 2f - 1f, 0f, 1f));
        float k = size.Selected * CenterScales[rings - 1] * centerShrink;
        float halfW = (CenterBoxWidth * 0.5f + CardPadding) * k;
        float top = CenterY - (CenterBoxHeight * 0.5f + CardPadding) * k;
        float bottom = CenterY + (CenterBoxHeight * 0.5f + CardPadding) * k + InfoStripHeight * k * CardSizing.TypeFactor(size.Selected);
        return DesignSpace.Width * 0.5f - halfW >= bounds.Min.X && DesignSpace.Width * 0.5f + halfW <= bounds.Max.X &&
               top >= bounds.Min.Y && bottom <= bounds.Max.Y;
    }

    // ───────────────────────────── path ─────────────────────────────

    private void BuildPath(int n, int rings, int[] capacities, double half, float arc)
    {
        _path.Clear();
        _path.Add(new PathPoint(0, 0f, 0f));

        if (n <= 2)
        {
            _path.Add(new PathPoint(1, Gather(78f, arc) * Deg, 0f));
            return;
        }

        Span<float> angles = stackalloc float[n];
        double start = 0;
        float previousLast = 0f;
        for (int r = 0; r < rings; r++)
        {
            bool last = r == rings - 1;
            double end = last ? half : (r == 0 ? capacities[0] + 0.5 : start + capacities[r]);

            // Resting slots of this orbit: the whole path distances in (start, end].
            double firstSlot = Math.Floor(start) + 1;
            if (firstSlot <= start + 1e-9) firstSlot += 1;
            int slotCount = Math.Max(0, (int)Math.Floor(end + 1e-9 - firstSlot) + 1);

            bool meeting = last && slotCount > 0 && Math.Abs(firstSlot + slotCount - 1 - end) < 1e-9;
            int spread = slotCount - (meeting ? 1 : 0);
            var ringAngles = angles[..spread];
            SlotAngles(r, arc, ringAngles);
            bool ascending = r % 2 == 0;

            if (r > 0 && spread > 0)
            {
                // Orbit transfer: halfway between the last slot of the inner orbit and the first of this one.
                float first = (ascending ? ringAngles[0] : ringAngles[^1]) * Deg;
                _path.Add(new PathPoint(start, (previousLast + first) * 0.5f, r - 0.5f));
            }

            for (int k = 0; k < spread; k++)
            {
                float a = (ascending ? ringAngles[k] : ringAngles[spread - 1 - k]) * Deg;
                _path.Add(new PathPoint(firstSlot + k, a, r));
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

    /// <summary>
    /// Resting angles (degrees from the front) for the slots of one orbit, front to back.
    /// <paramref name="arc"/> gathers them toward the flank (90°), away from the selected card.
    /// </summary>
    private static void SlotAngles(int ring, float arc, Span<float> angles)
    {
        int count = angles.Length;
        if (count <= 0) return;
        if (ring == 0)
        {
            if (count == 1) angles[0] = 78f;
            else if (count == 2) { angles[0] = 62f; angles[1] = 128f; }
            else Linspace(54f, 146f, angles);
        }
        else if (count == 1)
        {
            angles[0] = ring % 2 == 1 ? 100f : 124f;
        }
        else if (ring % 2 == 1)
        {
            // Neighbouring orbits must not put their end slots at the same angle, or one card sits right
            // behind the other where the path hands over (front) or turns (back). Odd orbits span the
            // full flank, even outer orbits a narrower arc, so the ends interleave.
            Linspace(34f, 160f, angles);
        }
        else
        {
            Linspace(60f, 136f, angles);
        }
        for (int i = 0; i < count; i++) angles[i] = Gather(angles[i], arc);
    }

    /// <summary>Moves an angle toward the flank; at most halfway, so slots keep their order and spacing.</summary>
    private static float Gather(float degrees, float arc) => 90f + (degrees - 90f) * (1f - 0.5f * arc);

    private static void Linspace(float from, float to, Span<float> into)
    {
        int count = into.Length;
        for (int i = 0; i < count; i++) into[i] = from + (to - from) * i / (count - 1);
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
