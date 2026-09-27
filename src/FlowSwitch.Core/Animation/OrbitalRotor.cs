namespace FlowSwitch.Core.Animation;

/// <summary>
/// The single physical state behind every selection change: a continuous "rotor" position that
/// chases an integer target with a spring. Rapid Tab presses never start competing animations —
/// they only move the target further, which the rotor follows with accumulated momentum
/// (it visibly speeds up), then settles softly once presses stop.
/// </summary>
/// <remarks>
/// Positions are unwrapped (they may exceed the item count or go negative) so wrapping from the
/// last item back to the first keeps rotating in the same direction instead of spinning back.
/// </remarks>
public sealed class OrbitalRotor
{
    private double _velocity;

    /// <summary>Continuous, unwrapped position. Integer values mean "item at rest in the centre".</summary>
    public double Position { get; private set; }

    /// <summary>Unwrapped integer target.</summary>
    public long Target { get; private set; }

    public double Velocity => _velocity;

    /// <summary>How far the visual is behind the logical selection, in items.</summary>
    public double Lag => Target - Position;

    /// <summary>Maximum number of items the visual may trail behind. Beyond this the rotor jumps ahead.</summary>
    public double MaxLag { get; set; } = 2.6;

    /// <summary>How strongly a backlog of presses stiffens the spring (0 = constant speed).</summary>
    public double CatchUp { get; set; } = 0.55;

    public bool IsSettled => Math.Abs(Lag) < 1e-3 && Math.Abs(_velocity) < 1e-2;

    public void Reset(long index)
    {
        Target = index;
        Position = index;
        _velocity = 0;
    }

    /// <summary>Moves the logical selection by <paramref name="delta"/> items.</summary>
    public void Advance(int delta) => Target += delta;

    /// <summary>Sets an absolute target (non-wrapping layouts).</summary>
    public void SetTarget(long target) => Target = target;

    /// <summary>Jumps the logical selection to the nearest unwrapped equivalent of <paramref name="index"/>.</summary>
    public void SeekTo(int index, int count)
    {
        if (count <= 0) { Target = index; return; }
        long current = Target;
        long baseCycle = FloorDiv(current, count) * count;
        long best = baseCycle + index;
        foreach (long candidate in new[] { best - count, best, best + count })
        {
            if (Math.Abs(candidate - current) < Math.Abs(best - current)) best = candidate;
        }
        Target = best;
    }

    /// <summary>Advances the physics. Returns true while still moving.</summary>
    public bool Step(float dt, in SpringSpec spec)
    {
        double lag = Target - Position;

        // Never let the visual fall hopelessly behind a burst of presses: keep momentum, drop distance.
        if (Math.Abs(lag) > MaxLag)
        {
            Position = Target - Math.Sign(lag) * MaxLag;
            lag = Target - Position;
        }

        // A backlog stiffens the spring, so Tab-Tab-Tab visibly accelerates the whole system.
        double backlog = Math.Max(0, Math.Abs(lag) - 1.0);
        double response = spec.Response / (1.0 + CatchUp * backlog);
        response = Math.Max(response, spec.Response * 0.4);
        double omega = Math.Tau / response;

        double x = -lag;
        SpringMath.Step(ref x, ref _velocity, dt, omega, spec.DampingRatio);
        Position = Target + x;

        if (Math.Abs(x) < 1e-4 && Math.Abs(_velocity) < 1e-3)
        {
            Position = Target;
            _velocity = 0;
            return false;
        }
        return true;
    }

    /// <summary>The selected index for a list of <paramref name="count"/> items.</summary>
    public int SelectedIndex(int count) => count <= 0 ? 0 : (int)Mod(Target, count);

    /// <summary>
    /// Signed ring offset of item <paramref name="index"/> relative to the continuous position,
    /// wrapped into (−count/2, count/2].
    /// </summary>
    public double OffsetOf(int index, int count) => WrapOffset(index - Position, count);

    public static double WrapOffset(double d, int count)
    {
        if (count <= 0) return d;
        double half = count / 2.0;
        d %= count;
        if (d > half) d -= count;
        else if (d <= -half) d += count;
        return d;
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor(a / (double)b);

    private static long Mod(long a, long b) => ((a % b) + b) % b;
}
