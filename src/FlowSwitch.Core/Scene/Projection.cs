using System.Numerics;

namespace FlowSwitch.Core.Scene;

/// <summary>
/// CPU mirror of the vertex shader transform (VSQuad in FlowSwitch.hlsl), used for hit-testing.
/// Keep both in sync.
/// </summary>
public static class Projection
{
    public static Vector2 Project(Vector2 local, Vector4 p0, Vector4 p2, in FrameConstants frame)
    {
        float cr = MathF.Cos(p2.Z), sr = MathF.Sin(p2.Z);
        float cy = MathF.Cos(p2.X), sy = MathF.Sin(p2.X);
        float cp = MathF.Cos(p2.Y), sp = MathF.Sin(p2.Y);

        var p = new Vector3(local.X * cr - local.Y * sr, local.X * sr + local.Y * cr, 0f);
        p = new Vector3(p.X * cy, p.Y, p.X * sy);
        p = new Vector3(p.X, p.Y * cp - p.Z * sp, p.Y * sp + p.Z * cp);
        p *= p0.Z;

        var world = new Vector3(p0.X + p.X, p0.Y + p.Y, p.Z + p2.W);
        float focal = frame.Camera.Y;
        float w = MathF.Max(0.05f, (focal + world.Z) / focal);
        var vp = new Vector2(frame.Camera.Z, frame.Camera.W);
        return vp + (new Vector2(world.X, world.Y) - vp) / w;
    }

    public static bool PointInQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        // Convex quad a-b-c-d (any winding).
        float s1 = Cross(b - a, p - a), s2 = Cross(c - b, p - b), s3 = Cross(d - c, p - c), s4 = Cross(a - d, p - d);
        bool neg = s1 < 0 || s2 < 0 || s3 < 0 || s4 < 0;
        bool pos = s1 > 0 || s2 > 0 || s3 > 0 || s4 > 0;
        return !(neg && pos);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
