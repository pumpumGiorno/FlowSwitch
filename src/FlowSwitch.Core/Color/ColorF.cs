using System.Globalization;
using System.Numerics;

namespace FlowSwitch.Core.Color;

/// <summary>An sRGB colour with straight (non-premultiplied) alpha, components in 0..1.</summary>
public readonly record struct ColorF(float R, float G, float B, float A = 1f)
{
    public static ColorF White => new(1, 1, 1);
    public static ColorF Black => new(0, 0, 0);
    public static ColorF Transparent => new(0, 0, 0, 0);

    /// <summary>A calm steel-blue used when an app has no usable accent colour.</summary>
    public static ColorF NeutralAccent => FromHex("#7C8CB8");

    public static ColorF FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    public static ColorF FromHex(string hex)
    {
        if (!TryParseHex(hex, out var c)) throw new FormatException($"Invalid colour '{hex}'.");
        return c;
    }

    public static bool TryParseHex(string? hex, out ColorF color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.StartsWith("#")) s = s[1..];
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        if (s.Length == 6) v = (v << 8) | 0xFF;
        color = FromBytes((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string ToHex() =>
        $"#{ToByte(R):X2}{ToByte(G):X2}{ToByte(B):X2}" + (A < 0.999f ? $"{ToByte(A):X2}" : string.Empty);

    public ColorF WithAlpha(float a) => this with { A = a };

    public Vector3 Rgb => new(R, G, B);

    public Vector4 ToVector4() => new(R, G, B, A);

    /// <summary>Linear-light RGB (for lighting maths in shaders).</summary>
    public Vector3 ToLinear() => new(SrgbToLinear(R), SrgbToLinear(G), SrgbToLinear(B));

    public static ColorF FromLinear(Vector3 lin, float a = 1f) =>
        new(LinearToSrgb(lin.X), LinearToSrgb(lin.Y), LinearToSrgb(lin.Z), a);

    public float Luminance
    {
        get
        {
            var l = ToLinear();
            return 0.2126f * l.X + 0.7152f * l.Y + 0.0722f * l.Z;
        }
    }

    public static ColorF Lerp(ColorF a, ColorF b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>Perceptual interpolation through OKLab (no muddy grey midpoints).</summary>
    public static ColorF LerpOklab(ColorF a, ColorF b, float t)
    {
        var la = OkLab.FromSrgb(a);
        var lb = OkLab.FromSrgb(b);
        return OkLab.ToSrgb(Vector3.Lerp(la, lb, t), a.A + (b.A - a.A) * t);
    }

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    public override string ToString() => ToHex();
}
