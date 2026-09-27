using System.Numerics;

namespace FlowSwitch.Core.Scene;

/// <summary>
/// Text sprites are emitted with an anchor instead of a rectangle because only the backend knows
/// the rasterised size. Backends call <see cref="Resolve"/> once the texture exists.
/// <code>
/// P4 = anchor.x, anchor.y, alignX (0 left … 1 right), alignY (0 top … 1 bottom)   (local px)
/// P7 = lod bias, 0, 1 (text flag), raster-to-local scale
/// </code>
/// </summary>
public static class TextPlacement
{
    public const float TextFlag = 1f;

    public static bool IsText(in DrawCommand cmd) => cmd.Shader == ShaderKind.Sprite && cmd.P[7].Z == TextFlag;

    public static void Resolve(ref DrawCommand cmd, Vector2 textureSizePx)
    {
        Vector4 anchor = cmd.P[4];
        float k = cmd.P[7].W <= 0 ? 1f : cmd.P[7].W;
        Vector2 size = textureSizePx * k;
        float x0 = anchor.X - anchor.Z * size.X;
        float y0 = anchor.Y - anchor.W * size.Y;
        cmd.P[1] = new Vector4(size * 0.5f, 0f, 0f);
        cmd.P[3] = new Vector4(x0 + size.X * 0.5f, y0 + size.Y * 0.5f, 0f, 0f);
        cmd.P[4] = new Vector4(x0, y0, x0 + size.X, y0 + size.Y);
        cmd.P[5] = new Vector4(0f, 0f, 1f, 1f);
        cmd.P[7] = cmd.P[7] with { Z = 0f };
    }
}
