namespace FlowSwitch.Core.Settings;

/// <summary>Concrete rendering budget derived from a <see cref="QualityPreset"/>.</summary>
public sealed record QualityProfile
{
    public required QualityPreset Preset { get; init; }

    /// <summary>Largest edge of a cached window preview, in pixels. 0 = native resolution.</summary>
    public int PreviewMaxDimension { get; init; }

    /// <summary>Preview refresh rate for the selected card. 0 = every rendered frame.</summary>
    public float SelectedPreviewFps { get; init; }

    /// <summary>Preview refresh rate for cards on the inner orbit / neighbours.</summary>
    public float NearPreviewFps { get; init; }

    /// <summary>Preview refresh rate for distant cards. 0 = last captured frame only.</summary>
    public float FarPreviewFps { get; init; }

    public bool LivePreviews { get; init; } = true;

    /// <summary>Number of blur pyramid levels built from the desktop capture.</summary>
    public int BackdropBlurLevels { get; init; }

    /// <summary>How often the blurred desktop behind the overlay is refreshed. 0 = captured once.</summary>
    public float BackdropFps { get; init; }

    public int ParticleCount { get; init; }

    /// <summary>Frosted glass samples the blurred backdrop behind each card.</summary>
    public bool GlassFrost { get; init; } = true;

    public bool DepthOfField { get; init; } = true;

    public bool MotionTrailsAllowed { get; init; }

    public bool Prewarm { get; init; } = true;

    public static QualityProfile BatterySaver { get; } = new()
    {
        Preset = QualityPreset.BatterySaver,
        PreviewMaxDimension = 480,
        SelectedPreviewFps = 12,
        NearPreviewFps = 0,
        FarPreviewFps = 0,
        BackdropBlurLevels = 4,
        BackdropFps = 0,
        ParticleCount = 0,
        GlassFrost = false,
        DepthOfField = false,
        MotionTrailsAllowed = false,
        Prewarm = false,
    };

    public static QualityProfile Balanced { get; } = new()
    {
        Preset = QualityPreset.Balanced,
        PreviewMaxDimension = 960,
        SelectedPreviewFps = 0,
        NearPreviewFps = 30,
        FarPreviewFps = 8,
        BackdropBlurLevels = 5,
        BackdropFps = 12,
        ParticleCount = 18,
        GlassFrost = true,
        DepthOfField = true,
        MotionTrailsAllowed = false,
    };

    public static QualityProfile High { get; } = new()
    {
        Preset = QualityPreset.High,
        PreviewMaxDimension = 1440,
        SelectedPreviewFps = 0,
        NearPreviewFps = 30,
        FarPreviewFps = 12,
        BackdropBlurLevels = 6,
        BackdropFps = 30,
        ParticleCount = 32,
        GlassFrost = true,
        DepthOfField = true,
        MotionTrailsAllowed = true,
    };

    public static QualityProfile Ultra { get; } = new()
    {
        Preset = QualityPreset.Ultra,
        PreviewMaxDimension = 0,
        SelectedPreviewFps = 0,
        NearPreviewFps = 60,
        FarPreviewFps = 30,
        BackdropBlurLevels = 6,
        BackdropFps = 60,
        ParticleCount = 56,
        GlassFrost = true,
        DepthOfField = true,
        MotionTrailsAllowed = true,
    };

    /// <summary>Resolves the effective profile, taking power state into account for <see cref="QualityPreset.Auto"/>.</summary>
    public static QualityProfile Resolve(PerformanceSettings settings, PowerState power)
    {
        var preset = settings.Quality;
        if (preset == QualityPreset.Auto)
        {
            preset = power switch
            {
                PowerState.BatterySaver => QualityPreset.BatterySaver,
                PowerState.Battery => QualityPreset.Balanced,
                _ => QualityPreset.High,
            };
        }

        var profile = preset switch
        {
            QualityPreset.BatterySaver => BatterySaver,
            QualityPreset.Balanced => Balanced,
            QualityPreset.Ultra => Ultra,
            _ => High,
        };

        return settings.LivePreviews ? profile : profile with { LivePreviews = false, SelectedPreviewFps = 0, NearPreviewFps = 0, FarPreviewFps = 0 };
    }

    /// <summary>One step down, used by adaptive quality when frames run long.</summary>
    public QualityProfile Degraded() => this with
    {
        ParticleCount = ParticleCount / 2,
        BackdropFps = Math.Min(BackdropFps, 10),
        NearPreviewFps = Math.Min(NearPreviewFps, 20),
        FarPreviewFps = Math.Min(FarPreviewFps, 5),
        GlassFrost = Preset >= QualityPreset.High && GlassFrost,
        MotionTrailsAllowed = false,
    };
}

public enum PowerState
{
    AC,
    Battery,
    BatterySaver,
}
