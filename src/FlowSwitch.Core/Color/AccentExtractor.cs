using System.Numerics;

namespace FlowSwitch.Core.Color;

/// <summary>
/// Finds the characteristic colour of an app icon: a chroma-weighted hue histogram in OKLab,
/// so a small saturated logo on a grey background still wins over the background.
/// </summary>
public static class AccentExtractor
{
    private const int HueBins = 36;

    /// <summary>
    /// Extracts the dominant accent colour from 32-bit BGRA pixels.
    /// Returns null when the icon is essentially colourless.
    /// </summary>
    public static ColorF? Extract(ReadOnlySpan<byte> bgra, int width, int height, int stride, bool premultiplied)
    {
        if (width <= 0 || height <= 0 || bgra.Length < stride * (height - 1) + width * 4) return null;

        Span<float> weight = stackalloc float[HueBins];
        Span<float> sumA = stackalloc float[HueBins];
        Span<float> sumB = stackalloc float[HueBins];
        Span<float> sumL = stackalloc float[HueBins];
        Span<float> sumC = stackalloc float[HueBins];
        weight.Clear(); sumA.Clear(); sumB.Clear(); sumL.Clear(); sumC.Clear();

        int step = Math.Max(1, Math.Max(width, height) / 64);
        float opaqueWeight = 0f, chromaWeight = 0f;
        Vector3 greySum = Vector3.Zero;

        for (int y = 0; y < height; y += step)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += step)
            {
                int i = row + x * 4;
                float a = bgra[i + 3] / 255f;
                if (a < 0.35f) continue;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;
                if (premultiplied) { r /= a; g /= a; b /= a; }
                var lab = OkLab.FromSrgb(new ColorF(Math.Min(r, 1f), Math.Min(g, 1f), Math.Min(b, 1f)));
                var (l, c, h) = OkLab.ToLch(lab);

                opaqueWeight += a;
                greySum += lab * a;
                if (c < 0.035f) continue;

                // Penalise near-black and near-white pixels; they carry little identity.
                float lightness = Easing01(l, 0.18f, 0.35f) * (1f - Easing01(l, 0.90f, 0.98f));
                float w = a * MathF.Pow(c, 1.3f) * lightness;
                if (w <= 0f) continue;

                int bin = (int)((h + MathF.PI) / MathF.Tau * HueBins) % HueBins;
                weight[bin] += w;
                sumA[bin] += lab.Y * w;
                sumB[bin] += lab.Z * w;
                sumL[bin] += l * w;
                sumC[bin] += c * w;
                chromaWeight += w;
            }
        }

        if (opaqueWeight <= 0f) return null;
        // Colourless icons (monochrome logos) → no accent; caller falls back to a neutral glow.
        if (chromaWeight / opaqueWeight < 0.004f) return null;

        int best = 0;
        float bestScore = -1f;
        for (int i = 0; i < HueBins; i++)
        {
            float score = weight[i] + 0.5f * (weight[(i + 1) % HueBins] + weight[(i + HueBins - 1) % HueBins]);
            if (score > bestScore) { bestScore = score; best = i; }
        }

        float tw = 0f, ta = 0f, tb = 0f, tl = 0f, tc = 0f;
        for (int k = -1; k <= 1; k++)
        {
            int i = (best + k + HueBins) % HueBins;
            tw += weight[i]; ta += sumA[i]; tb += sumB[i]; tl += sumL[i]; tc += sumC[i];
        }
        if (tw <= 0f) return null;

        float hue = MathF.Atan2(tb / tw, ta / tw);
        return OkLab.GamutMap(tl / tw, tc / tw, hue);
    }

    /// <summary>
    /// Normalises any colour into a glow-friendly range: luminous enough to read on a near-black
    /// background, but never neon. Hue is preserved.
    /// </summary>
    public static ColorF NormalizeForGlow(ColorF color)
    {
        var (l, c, h) = OkLab.ToLch(OkLab.FromSrgb(color));
        if (c < 0.03f)
        {
            // Treat as neutral: a cool, slightly blue graphite light.
            return OkLab.GamutMap(0.74f, 0.035f, 4.5f);
        }
        l = Math.Clamp(l, 0.64f, 0.80f);
        c = Math.Clamp(c, 0.08f, 0.17f);
        return OkLab.GamutMap(l, c, h);
    }

    private static float Easing01(float v, float a, float b) => Math.Clamp((v - a) / (b - a), 0f, 1f);
}
