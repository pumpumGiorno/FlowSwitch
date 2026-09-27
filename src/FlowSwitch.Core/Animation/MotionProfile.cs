using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Animation;

/// <summary>
/// The FlowSwitch animation language, resolved for the current preset. Every moving thing in the
/// overlay reads its timing from here, so motion stays coherent across the whole product.
/// See docs/MOTION.md for the rationale behind each token.
/// </summary>
/// <remarks>
/// Principle: responsive first, cinematic second. Nothing the user waits on may take longer than
/// ~0.3 s to become readable; long tails are only allowed on light, colour and ambient motion.
/// </remarks>
public sealed record MotionProfile
{
    /// <summary>Hover, press, small highlights (100–160 ms feel).</summary>
    public SpringSpec Micro { get; init; } = new(0.16f, 1f);

    /// <summary>The orbital rotation that follows selection (250–400 ms feel for large moves).</summary>
    public SpringSpec Rotor { get; init; } = new(0.30f, 0.86f);

    /// <summary>Re-layout: search filtering, windows opening/closing, grouping (180–280 ms feel).</summary>
    public SpringSpec Layout { get; init; } = new(0.36f, 0.90f);

    /// <summary>Cards appearing / disappearing.</summary>
    public SpringSpec Presence { get; init; } = new(0.24f, 1f);

    /// <summary>Ambient light colour following the selected app (≈230 ms to 95%).</summary>
    public SpringSpec Color { get; init; } = new(0.30f, 1f);

    /// <summary>Compact → expanded stage.</summary>
    public SpringSpec Stage { get; init; } = new(0.40f, 0.92f);

    public SpringSpec Parallax { get; init; } = new(0.60f, 1f);

    public float RevealDuration { get; init; } = 0.20f;
    public float RevealBlurDuration { get; init; } = 0.26f;
    public float RevealStagger { get; init; } = 0.022f;
    public float ExitDuration { get; init; } = 0.19f;
    public float CancelDuration { get; init; } = 0.16f;
    public float CrossfadeDuration { get; init; } = 0.14f;

    /// <summary>Peak scale change of the selected card's "breathing" (0.012 = 1.2%).</summary>
    public float BreathingAmplitude { get; init; } = 0.012f;
    public float BreathingPeriod { get; init; } = 4.6f;

    /// <summary>0..1 multiplier for idle drift (orbit sway, floating cards, glow shimmer).</summary>
    public float IdleAmount { get; init; } = 1f;

    public float ScaleIntensity { get; init; } = 1f;
    public float RotationIntensity { get; init; } = 1f;
    public float DepthIntensity { get; init; } = 1f;
    public float ParallaxAmount { get; init; } = 0.5f;
    public bool MotionTrails { get; init; }

    /// <summary>Reduced motion: no travel, no parallax, no idle drift — selection cross-fades in place.</summary>
    public bool Reduced { get; init; }

    public static MotionProfile Smooth { get; } = new();

    public static MotionProfile Snappy { get; } = new()
    {
        Micro = new(0.12f, 1f),
        Rotor = new(0.22f, 0.95f),
        Layout = new(0.28f, 0.95f),
        Presence = new(0.18f, 1f),
        Color = new(0.22f, 1f),
        Stage = new(0.30f, 0.95f),
        RevealDuration = 0.15f,
        RevealBlurDuration = 0.20f,
        RevealStagger = 0.014f,
        ExitDuration = 0.15f,
        CancelDuration = 0.13f,
        BreathingAmplitude = 0.008f,
    };

    public static MotionProfile Cinematic { get; } = new()
    {
        Micro = new(0.20f, 0.95f),
        Rotor = new(0.38f, 0.82f),
        Layout = new(0.46f, 0.84f),
        Presence = new(0.30f, 0.95f),
        Color = new(0.36f, 1f),
        Stage = new(0.50f, 0.88f),
        RevealDuration = 0.26f,
        RevealBlurDuration = 0.34f,
        RevealStagger = 0.03f,
        ExitDuration = 0.23f,
        CancelDuration = 0.19f,
        BreathingAmplitude = 0.016f,
        IdleAmount = 1.3f,
        ScaleIntensity = 1.1f,
        RotationIntensity = 1.15f,
    };

    public static MotionProfile Minimal { get; } = new()
    {
        Micro = new(0.12f, 1f),
        Rotor = new(0.24f, 1f),
        Layout = new(0.30f, 1f),
        Presence = new(0.18f, 1f),
        Color = new(0.24f, 1f),
        Stage = new(0.30f, 1f),
        RevealDuration = 0.14f,
        RevealBlurDuration = 0.18f,
        RevealStagger = 0f,
        ExitDuration = 0.13f,
        CancelDuration = 0.12f,
        BreathingAmplitude = 0f,
        IdleAmount = 0f,
        ScaleIntensity = 0.5f,
        RotationIntensity = 0.4f,
        ParallaxAmount = 0f,
    };

    public static MotionProfile ReducedMotion { get; } = new()
    {
        Micro = new(0.12f, 1f),
        Rotor = new(0.12f, 1f),
        Layout = new(0.20f, 1f),
        Presence = new(0.16f, 1f),
        Color = new(0.24f, 1f),
        Stage = new(0.20f, 1f),
        RevealDuration = 0.14f,
        RevealBlurDuration = 0.18f,
        RevealStagger = 0f,
        ExitDuration = 0.12f,
        CancelDuration = 0.12f,
        CrossfadeDuration = 0.14f,
        BreathingAmplitude = 0f,
        IdleAmount = 0f,
        ScaleIntensity = 0.35f,
        RotationIntensity = 0f,
        DepthIntensity = 0.8f,
        ParallaxAmount = 0f,
        MotionTrails = false,
        Reduced = true,
    };

    public static MotionProfile Resolve(AnimationSettings settings, SolarSystemSettings solar, bool systemPrefersReducedMotion)
    {
        bool reduced = settings.ReducedMotion switch
        {
            ReducedMotionMode.On => true,
            ReducedMotionMode.Off => false,
            _ => systemPrefersReducedMotion,
        };
        if (reduced) return ReducedMotion;

        MotionProfile p = settings.Preset switch
        {
            AnimationPreset.Snappy => Snappy,
            AnimationPreset.Cinematic => Cinematic,
            AnimationPreset.Minimal => Minimal,
            AnimationPreset.Custom => Custom(settings),
            _ => Smooth,
        };

        float idle = solar.IdleRotation ? p.IdleAmount * (0.35f + 1.3f * solar.IdleRotationSpeed) : 0f;
        return p with
        {
            IdleAmount = idle,
            BreathingAmplitude = settings.Breathing ? p.BreathingAmplitude : 0f,
            ScaleIntensity = p.ScaleIntensity * settings.ScaleIntensity,
            RotationIntensity = p.RotationIntensity * settings.RotationIntensity,
            DepthIntensity = p.DepthIntensity * settings.DepthIntensity,
            ParallaxAmount = solar.Parallax ? p.ParallaxAmount * (settings.Parallax / 0.5f) : 0f,
            MotionTrails = settings.MotionBlur,
        };
    }

    private static MotionProfile Custom(AnimationSettings s)
    {
        float scale = s.DurationScale;
        float response = Easing.Lerp(0.46f, 0.18f, s.SpringStiffness) * scale;
        float zeta = s.SpringDamping;
        return Smooth with
        {
            Micro = new(MathF.Max(0.08f, 0.16f * scale), 1f),
            Rotor = new(response, zeta),
            Layout = new(response * 1.2f, MathF.Min(1f, zeta + 0.04f)),
            Presence = new(0.24f * scale, 1f),
            Color = new(0.30f * scale, 1f),
            Stage = new(0.40f * scale, MathF.Min(1f, zeta + 0.04f)),
            RevealDuration = MathF.Min(0.32f, 0.20f * scale),
            RevealBlurDuration = MathF.Min(0.40f, 0.26f * scale),
            ExitDuration = MathF.Min(0.26f, 0.19f * scale),
            CancelDuration = MathF.Min(0.22f, 0.16f * scale),
        };
    }
}
