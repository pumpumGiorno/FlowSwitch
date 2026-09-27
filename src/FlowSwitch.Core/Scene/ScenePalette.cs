using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Scene;

/// <summary>Resolved look of the overlay for the current theme and appearance settings.</summary>
public sealed record ScenePalette
{
    /// <summary>Colour the blurred desktop is graded towards (deep blue-black).</summary>
    public ColorF BaseTint { get; init; } = ColorF.FromHex("#070A12");
    /// <summary>Body colour of glass surfaces.</summary>
    public ColorF GlassTint { get; init; } = ColorF.FromHex("#131826");
    /// <summary>How strongly the desktop is darkened (0..1).</summary>
    public float Dim { get; init; } = 0.62f;
    public float GlassOpacity { get; init; } = 0.8f;
    public float BorderStrength { get; init; } = 0.5f;
    public float GlowStrength { get; init; } = 0.7f;
    public float GlowRadius { get; init; } = 0.6f;
    public float AmbientStrength { get; init; } = 1f;
    public float OrbitAlpha { get; init; } = 0.5f;
    public float ParticleAlpha { get; init; } = 0.35f;
    public float Grain { get; init; } = 0.018f;
    public float CornerRadius { get; init; } = 22f;
    public float FontScale { get; init; } = 1f;
    public bool Oled { get; init; }
    public bool AdaptiveExposure { get; init; }
    public AmbientColorSource AmbientSource { get; init; } = AmbientColorSource.FromApp;
    public ColorF AmbientCustom { get; init; } = ColorF.FromHex("#5B6CFF");
    /// <summary>0..1: fraction of the blur pyramid used behind the overlay.</summary>
    public float BlurAmount { get; init; } = 0.75f;
    /// <summary>Orbit-minimal style: quieter glass, fewer lights.</summary>
    public bool Minimal { get; init; }

    public static ScenePalette Resolve(AppearanceSettings a, SolarSystemSettings solar, SwitcherMode mode)
    {
        bool minimal = mode == SwitcherMode.OrbitMinimal;
        bool oled = a.Theme == ThemeMode.Oled;
        var palette = new ScenePalette
        {
            BaseTint = oled ? ColorF.Black : ColorF.FromHex("#070A12"),
            GlassTint = oled ? ColorF.FromHex("#07080C") : ColorF.FromHex("#131826"),
            Dim = oled ? Easing.Lerp(0.6f, 0.95f, a.BackgroundDarkness) : Easing.Lerp(0.12f, 0.76f, a.BackgroundDarkness),
            GlassOpacity = oled ? MathF.Min(1f, a.CardOpacity + 0.12f) : a.CardOpacity,
            BorderStrength = a.BorderIntensity,
            GlowStrength = a.GlowIntensity * (minimal ? 0.45f : 1f) * Easing.Lerp(0.6f, 1.2f, solar.Glow),
            GlowRadius = a.GlowRadius,
            AmbientStrength = (oled ? 0.7f : 1f) * (minimal ? 0.5f : 1f) * Easing.Lerp(0.3f, 1.3f, solar.AmbientLight),
            OrbitAlpha = solar.OrbitLines ? a.OrbitVisibility * (minimal ? 0.8f : 1f) : 0f,
            ParticleAlpha = solar.Particles && !minimal ? a.ParticleIntensity : 0f,
            Grain = oled ? 0.012f : 0.018f,
            CornerRadius = a.CardCornerRadius,
            FontScale = a.FontScale,
            Oled = oled,
            AdaptiveExposure = a.Theme == ThemeMode.Auto,
            AmbientSource = a.AmbientSource,
            AmbientCustom = ColorF.TryParseHex(a.AmbientCustomColor, out var c) ? c : ColorF.FromHex("#5B6CFF"),
            BlurAmount = a.BlurIntensity,
            Minimal = minimal,
        };
        return palette;
    }

    public static Vector3 Rgb(ColorF c) => new(c.R, c.G, c.B);
}

/// <summary>What the renderer can currently offer for a window.</summary>
/// <param name="Aspect">Width / height of the captured content (0 = unknown).</param>
public readonly record struct PreviewInfo(bool Available, Vector4 Uv, bool IsLive, float Aspect = 0f)
{
    public static PreviewInfo Missing => new(false, new Vector4(0, 0, 1, 1), false);
}

/// <summary>Resource availability queried by the composer (implemented by the renderer backend).</summary>
public interface ISceneResources
{
    PreviewInfo GetPreview(long windowHandle);
    bool HasIcon(string appId);
    bool HasBackdrop { get; }
}

public sealed record DesktopInfo(string Name, int WindowCount, bool IsCurrent);
