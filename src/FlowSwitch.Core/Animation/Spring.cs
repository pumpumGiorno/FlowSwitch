using System.Numerics;

namespace FlowSwitch.Core.Animation;

/// <summary>
/// Closed-form damped harmonic oscillator. Using the analytic solution (instead of explicit
/// integration) keeps springs perfectly stable for any frame time — a 4 ms frame on a 240 Hz
/// display and a 50 ms hitch produce the same curve.
/// </summary>
public static class SpringMath
{
    /// <summary>Advances displacement <paramref name="x"/> (value − target) and velocity by <paramref name="dt"/>.</summary>
    public static void Step(ref double x, ref double v, double dt, double omega, double zeta)
    {
        if (dt <= 0) return;
        if (zeta < 0.999)
        {
            double wd = omega * Math.Sqrt(1 - zeta * zeta);
            double decay = Math.Exp(-zeta * omega * dt);
            double cos = Math.Cos(wd * dt), sin = Math.Sin(wd * dt);
            double b = (v + zeta * omega * x) / wd;
            double nx = decay * (x * cos + b * sin);
            double nv = decay * ((b * wd - zeta * omega * x) * cos - (x * wd + zeta * omega * b) * sin);
            x = nx; v = nv;
        }
        else if (zeta <= 1.001)
        {
            double decay = Math.Exp(-omega * dt);
            double b = v + omega * x;
            double nx = decay * (x + b * dt);
            double nv = decay * (v - omega * b * dt);
            x = nx; v = nv;
        }
        else
        {
            double s = Math.Sqrt(zeta * zeta - 1);
            double r1 = -omega * (zeta - s);
            double r2 = -omega * (zeta + s);
            double c2 = (v - r1 * x) / (r2 - r1);
            double c1 = x - c2;
            double e1 = Math.Exp(r1 * dt), e2 = Math.Exp(r2 * dt);
            x = c1 * e1 + c2 * e2;
            v = c1 * r1 * e1 + c2 * r2 * e2;
        }
    }
}

/// <summary>A single animated scalar driven by a spring.</summary>
public struct Spring
{
    public float Value;
    public float Velocity;
    public float Target;

    public Spring(float value) { Value = value; Target = value; Velocity = 0; }

    public readonly bool IsAtRest(float epsilon = 1e-3f) =>
        MathF.Abs(Value - Target) < epsilon && MathF.Abs(Velocity) < epsilon * 10;

    public void Snap(float value) { Value = value; Target = value; Velocity = 0; }

    public float Step(float dt, in SpringSpec spec)
    {
        double x = Value - Target, v = Velocity;
        SpringMath.Step(ref x, ref v, dt, spec.Omega, spec.DampingRatio);
        Value = (float)(Target + x);
        Velocity = (float)v;
        if (IsAtRest(1e-5f)) { Value = Target; Velocity = 0; }
        return Value;
    }

    public float Step(float target, float dt, in SpringSpec spec)
    {
        Target = target;
        return Step(dt, spec);
    }
}

/// <summary>A spring over <see cref="Vector2"/>, e.g. a parallax offset.</summary>
public struct Spring2
{
    public Vector2 Value;
    public Vector2 Velocity;
    public Vector2 Target;

    public Spring2(Vector2 value) { Value = value; Target = value; Velocity = default; }

    public void Snap(Vector2 value) { Value = value; Target = value; Velocity = default; }

    public Vector2 Step(float dt, in SpringSpec spec)
    {
        double w = spec.Omega, z = spec.DampingRatio;
        double x = Value.X - Target.X, vx = Velocity.X;
        double y = Value.Y - Target.Y, vy = Velocity.Y;
        SpringMath.Step(ref x, ref vx, dt, w, z);
        SpringMath.Step(ref y, ref vy, dt, w, z);
        Value = new Vector2((float)(Target.X + x), (float)(Target.Y + y));
        Velocity = new Vector2((float)vx, (float)vy);
        return Value;
    }
}

/// <summary>A spring over <see cref="Vector3"/>; used for colours in OKLab space.</summary>
public struct Spring3
{
    public Vector3 Value;
    public Vector3 Velocity;
    public Vector3 Target;

    public Spring3(Vector3 value) { Value = value; Target = value; Velocity = default; }

    public void Snap(Vector3 value) { Value = value; Target = value; Velocity = default; }

    public Vector3 Step(float dt, in SpringSpec spec)
    {
        double w = spec.Omega, z = spec.DampingRatio;
        double x = Value.X - Target.X, vx = Velocity.X;
        double y = Value.Y - Target.Y, vy = Velocity.Y;
        double q = Value.Z - Target.Z, vz = Velocity.Z;
        SpringMath.Step(ref x, ref vx, dt, w, z);
        SpringMath.Step(ref y, ref vy, dt, w, z);
        SpringMath.Step(ref q, ref vz, dt, w, z);
        Value = new Vector3((float)(Target.X + x), (float)(Target.Y + y), (float)(Target.Z + q));
        Velocity = new Vector3((float)vx, (float)vy, (float)vz);
        return Value;
    }
}
