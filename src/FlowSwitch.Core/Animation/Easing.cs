namespace FlowSwitch.Core.Animation;

/// <summary>CSS-style cubic-bézier timing curve, solved with Newton–Raphson plus bisection fallback.</summary>
public readonly struct CubicBezier
{
    private readonly float _cx, _bx, _ax, _cy, _by, _ay;

    public CubicBezier(float x1, float y1, float x2, float y2)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        _cx = 3f * x1; _bx = 3f * (x2 - x1) - _cx; _ax = 1f - _cx - _bx;
        _cy = 3f * y1; _by = 3f * (y2 - y1) - _cy; _ay = 1f - _cy - _by;
    }

    public float X1 { get; }
    public float Y1 { get; }
    public float X2 { get; }
    public float Y2 { get; }

    private float SampleX(float t) => ((_ax * t + _bx) * t + _cx) * t;
    private float SampleY(float t) => ((_ay * t + _by) * t + _cy) * t;
    private float SampleDX(float t) => (3f * _ax * t + 2f * _bx) * t + _cx;

    public float Evaluate(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        float t = x;
        for (int i = 0; i < 6; i++)
        {
            float err = SampleX(t) - x;
            if (MathF.Abs(err) < 1e-5f) return SampleY(t);
            float d = SampleDX(t);
            if (MathF.Abs(d) < 1e-6f) break;
            t -= err / d;
        }
        float lo = 0f, hi = 1f;
        t = x;
        for (int i = 0; i < 24; i++)
        {
            float sx = SampleX(t);
            if (MathF.Abs(sx - x) < 1e-5f) break;
            if (sx < x) lo = t; else hi = t;
            t = (lo + hi) * 0.5f;
        }
        return SampleY(t);
    }
}

/// <summary>The easing vocabulary of FlowSwitch. Every time-based animation uses one of these.</summary>
public static class Easing
{
    /// <summary>Things arriving on screen: fast start, long soft landing (Fluent "decelerate").</summary>
    public static readonly CubicBezier Enter = new(0.10f, 0.90f, 0.20f, 1.00f);

    /// <summary>Things leaving: gentle start, quick finish.</summary>
    public static readonly CubicBezier Exit = new(0.55f, 0.00f, 0.90f, 0.40f);

    /// <summary>On-screen transforms that are neither entering nor leaving.</summary>
    public static readonly CubicBezier Standard = new(0.20f, 0.00f, 0.00f, 1.00f);

    /// <summary>Very soft ease used for light / colour / blur ramps.</summary>
    public static readonly CubicBezier Soft = new(0.33f, 0.00f, 0.20f, 1.00f);

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Smootherstep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * x * (x * (x * 6f - 15f) + 10f);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static float InverseLerp(float a, float b, float v) =>
        MathF.Abs(b - a) < 1e-6f ? 0f : Math.Clamp((v - a) / (b - a), 0f, 1f);
}

/// <summary>A time-based animation from 0 to 1 with an easing curve.</summary>
public struct Tween
{
    public float Elapsed;
    public float Duration;
    public CubicBezier Curve;
    public float Delay;

    public Tween(float duration, CubicBezier curve, float delay = 0f)
    {
        Duration = MathF.Max(duration, 1e-4f);
        Curve = curve;
        Elapsed = 0f;
        Delay = delay;
    }

    public readonly float Linear => Math.Clamp((Elapsed - Delay) / Duration, 0f, 1f);
    public readonly float Value => Curve.Evaluate(Linear);
    public readonly bool IsComplete => Elapsed >= Delay + Duration;

    public float Step(float dt)
    {
        Elapsed += dt;
        return Value;
    }
}
