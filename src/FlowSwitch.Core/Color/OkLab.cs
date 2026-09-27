using System.Numerics;

namespace FlowSwitch.Core.Color;

/// <summary>
/// OKLab / OKLCh conversions (Björn Ottosson, 2020). Used for glow colour transitions and for
/// normalising extracted app colours to a consistent lightness and chroma.
/// </summary>
public static class OkLab
{
    public static Vector3 FromSrgb(ColorF c) => FromLinear(c.ToLinear());

    public static ColorF ToSrgb(Vector3 lab, float alpha = 1f) => ColorF.FromLinear(ToLinear(lab), alpha);

    public static Vector3 FromLinear(Vector3 rgb)
    {
        float l = 0.4122214708f * rgb.X + 0.5363325363f * rgb.Y + 0.0514459929f * rgb.Z;
        float m = 0.2119034982f * rgb.X + 0.6806995451f * rgb.Y + 0.1073969566f * rgb.Z;
        float s = 0.0883024619f * rgb.X + 0.2817188376f * rgb.Y + 0.6299787005f * rgb.Z;
        float l_ = MathF.Cbrt(l), m_ = MathF.Cbrt(m), s_ = MathF.Cbrt(s);
        return new Vector3(
            0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
            1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
            0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_);
    }

    public static Vector3 ToLinear(Vector3 lab)
    {
        float l_ = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m_ = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s_ = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        return new Vector3(
            +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    /// <summary>Lightness, chroma, hue (radians).</summary>
    public static (float L, float C, float H) ToLch(Vector3 lab) =>
        (lab.X, MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z), MathF.Atan2(lab.Z, lab.Y));

    public static Vector3 FromLch(float l, float c, float h) => new(l, c * MathF.Cos(h), c * MathF.Sin(h));

    /// <summary>
    /// Reduces chroma until the colour fits inside the sRGB gamut (keeps hue and lightness).
    /// </summary>
    public static ColorF GamutMap(float l, float c, float h, float alpha = 1f)
    {
        float lo = 0f, hi = c;
        Vector3 best = ToLinear(FromLch(l, 0f, h));
        for (int i = 0; i < 18; i++)
        {
            float mid = (lo + hi) * 0.5f;
            var rgb = ToLinear(FromLch(l, mid, h));
            if (InGamut(rgb)) { best = rgb; lo = mid; }
            else hi = mid;
        }
        var full = ToLinear(FromLch(l, c, h));
        if (InGamut(full)) best = full;
        return ColorF.FromLinear(best, alpha);
    }

    private static bool InGamut(Vector3 rgb) =>
        rgb.X >= -1e-4f && rgb.Y >= -1e-4f && rgb.Z >= -1e-4f &&
        rgb.X <= 1.0001f && rgb.Y <= 1.0001f && rgb.Z <= 1.0001f;
}
