using System.Numerics;
using System.Runtime.CompilerServices;

namespace FlowSwitch.Core.Scene;

/// <summary>Pixel shader programs. Must match the entry points in Shaders/FlowSwitch.hlsl.</summary>
public enum ShaderKind : byte
{
    Backdrop,
    Card,
    Sprite,
    Glow,
    Orbit,
    Pill,
}

public enum TextureKind : byte
{
    None,
    Preview,
    Icon,
    Text,
}

public enum FontFamilyKind : byte
{
    /// <summary>Segoe UI Variable Display — titles.</summary>
    Display,
    /// <summary>Segoe UI Variable Text — secondary text.</summary>
    Text,
    /// <summary>Segoe Fluent Icons (falls back to Segoe MDL2 Assets).</summary>
    Icons,
}

/// <summary>A run of text rendered once into a cached texture by the backend.</summary>
public sealed record TextSpec(string Text, float SizePx, int Weight, FontFamilyKind Family, float MaxWidthPx)
{
    public static TextSpec Create(string text, float sizePx, int weight, FontFamilyKind family, float maxWidthPx) =>
        new(text, MathF.Round(sizePx * 2f) / 2f, weight, family, MathF.Round(maxWidthPx / 8f) * 8f);
}

public readonly record struct TextureRef(TextureKind Kind, long Handle = 0, string? Key = null, TextSpec? Text = null)
{
    public static TextureRef None => default;
    public static TextureRef Preview(long hwnd) => new(TextureKind.Preview, Handle: hwnd);
    public static TextureRef Icon(string appId) => new(TextureKind.Icon, Key: appId);
    public static TextureRef ForText(TextSpec spec) => new(TextureKind.Text, Text: spec);
}

/// <summary>Twelve float4 shader constants (register b1 in HLSL).</summary>
[InlineArray(12)]
public struct ParamBlock
{
    private Vector4 _element0;
}

/// <summary>
/// One draw call. The layout of <see cref="P"/> is the contract with the shaders:
/// <code>
/// P0 = center.xy (px), scale, opacity          (quad shaders)
/// P1 = halfSize.xy, margin.xy (local px)
/// P2 = yaw, pitch, roll, z (px, into screen)
/// P3 = quadOffset.xy (local px), 0, 0
/// P4..P11 = shader-specific (see ShaderContract)
/// </code>
/// </summary>
public struct DrawCommand
{
    public ShaderKind Shader;
    public TextureRef Texture;
    public ParamBlock P;
}

/// <summary>Per-frame constants (register b0 in HLSL).</summary>
public struct FrameConstants
{
    /// <summary>width, height, 1/width, 1/height.</summary>
    public Vector4 Viewport;
    /// <summary>time, focal length (px), vanishing point x, y.</summary>
    public Vector4 Camera;
    /// <summary>backdrop max lod, frost lod bias, grain, OLED (0/1).</summary>
    public Vector4 Backdrop;
    /// <summary>has backdrop capture (0/1), adaptive exposure (0/1), overlay alpha, dither seed.</summary>
    public Vector4 Globals;
}

/// <summary>An ordered list of draw commands for one frame.</summary>
public sealed class DrawList
{
    private DrawCommand[] _items = new DrawCommand[256];

    public FrameConstants Frame;

    public int Count { get; private set; }

    public ReadOnlySpan<DrawCommand> Commands => _items.AsSpan(0, Count);

    public Span<DrawCommand> MutableCommands => _items.AsSpan(0, Count);

    public void Clear() => Count = 0;

    public ref DrawCommand Add(ShaderKind shader, TextureRef texture = default)
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        ref var cmd = ref _items[Count++];
        cmd = default;
        cmd.Shader = shader;
        cmd.Texture = texture;
        return ref cmd;
    }
}
