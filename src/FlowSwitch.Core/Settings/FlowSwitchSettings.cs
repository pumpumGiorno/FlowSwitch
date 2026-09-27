using System.Text.Json.Serialization;

namespace FlowSwitch.Core.Settings;

/// <summary>
/// Root of the user configuration (stored as JSON in %APPDATA%\FlowSwitch\settings.json).
/// Every default is chosen so that FlowSwitch looks finished without touching a single slider.
/// </summary>
public sealed class FlowSwitchSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public SolarSystemSettings Solar { get; set; } = new();
    public AnimationSettings Animation { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    public FlowSwitchSettings Clone() => SettingsSerializer.Deserialize(SettingsSerializer.Serialize(this));

    /// <summary>Clamps every value into its valid range (protects against hand-edited files).</summary>
    public FlowSwitchSettings Normalize()
    {
        General ??= new(); Appearance ??= new(); Solar ??= new(); Animation ??= new();
        Performance ??= new(); Hotkeys ??= new(); Advanced ??= new();
        General.Normalize(); Appearance.Normalize(); Solar.Normalize(); Animation.Normalize(); Advanced.Normalize();
        Version = CurrentVersion;
        return this;
    }

    internal static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0.5f;
    internal static float ClampF(float v, float min, float max, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

public sealed class GeneralSettings
{
    public bool Enabled { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;
    public SwitcherMode Mode { get; set; } = SwitcherMode.SolarSystem;
    public MonitorPlacement ShowOn { get; set; } = MonitorPlacement.ActiveWindow;
    public GroupingMode Grouping { get; set; } = GroupingMode.Auto;
    public bool ShowWindowsFromAllDesktops { get; set; }
    public bool ShowMinimizedWindows { get; set; } = true;
    public bool ShowVirtualDesktops { get; set; } = true;
    public bool SearchOnType { get; set; } = true;
    public bool WrapAround { get; set; } = true;
    /// <summary>Subtle glow on the target window when Alt+Tab is tapped too quickly to show the overlay.</summary>
    public bool QuickSwitchHighlight { get; set; } = true;
    /// <summary>A tap shorter than this switches instantly without showing the overlay.</summary>
    public int RevealDelayMs { get; set; } = 70;
    /// <summary>Holding Alt this long expands the UI (names, search, desktops, actions).</summary>
    public int ExpandDelayMs { get; set; } = 450;
    public bool OnboardingCompleted { get; set; }

    internal void Normalize()
    {
        RevealDelayMs = Math.Clamp(RevealDelayMs, 0, 400);
        ExpandDelayMs = Math.Clamp(ExpandDelayMs, 150, 2000);
    }
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public float BlurIntensity { get; set; } = 0.75f;
    public float BackgroundDarkness { get; set; } = 0.55f;
    public float GlowIntensity { get; set; } = 0.7f;
    public float GlowRadius { get; set; } = 0.6f;
    public float CardOpacity { get; set; } = 0.8f;
    /// <summary>Corner radius of the central card in design units (1600×900 reference space).</summary>
    public float CardCornerRadius { get; set; } = 22f;
    public float BorderIntensity { get; set; } = 0.5f;
    public float OrbitVisibility { get; set; } = 0.5f;
    public float ParticleIntensity { get; set; } = 0.35f;
    public AmbientColorSource AmbientSource { get; set; } = AmbientColorSource.FromApp;
    public string AmbientCustomColor { get; set; } = "#5B6CFF";
    public float FontScale { get; set; } = 1f;

    /// <summary>Size of the selected (centre) window's card, 1 = the designed size.</summary>
    public float SelectedCardSize { get; set; } = 1f;

    /// <summary>Size of every other card (orbits, rows, grid cells), 1 = the designed size.</summary>
    public float OrbitCardSize { get; set; } = 1f;

    /// <summary>Single card size of settings files written before the two sizes existed; migrated on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? CardSize { get; set; }

    public const float MinSelectedCardSize = 0.8f, MaxSelectedCardSize = 1.8f;
    public const float MinOrbitCardSize = 0.5f, MaxOrbitCardSize = 1.5f;

    internal void Normalize()
    {
        BlurIntensity = FlowSwitchSettings.Clamp01(BlurIntensity);
        BackgroundDarkness = FlowSwitchSettings.Clamp01(BackgroundDarkness);
        GlowIntensity = FlowSwitchSettings.Clamp01(GlowIntensity);
        GlowRadius = FlowSwitchSettings.Clamp01(GlowRadius);
        CardOpacity = FlowSwitchSettings.ClampF(CardOpacity, 0.3f, 1f, 0.8f);
        CardCornerRadius = FlowSwitchSettings.ClampF(CardCornerRadius, 6f, 40f, 22f);
        BorderIntensity = FlowSwitchSettings.Clamp01(BorderIntensity);
        OrbitVisibility = FlowSwitchSettings.Clamp01(OrbitVisibility);
        ParticleIntensity = FlowSwitchSettings.Clamp01(ParticleIntensity);
        FontScale = FlowSwitchSettings.ClampF(FontScale, 0.8f, 1.4f, 1f);
        if (CardSize is { } legacy)
        {
            SelectedCardSize = OrbitCardSize = legacy;
            CardSize = null;
        }
        SelectedCardSize = FlowSwitchSettings.ClampF(SelectedCardSize, MinSelectedCardSize, MaxSelectedCardSize, 1f);
        OrbitCardSize = FlowSwitchSettings.ClampF(OrbitCardSize, MinOrbitCardSize, MaxOrbitCardSize, 1f);
        AmbientCustomColor ??= "#5B6CFF";
    }
}

public sealed class SolarSystemSettings
{
    /// <summary>0 = round orbits, 1 = very flat, wide ellipses.</summary>
    public float OrbitShape { get; set; } = 0.5f;
    /// <summary>0 = automatic (by window count), otherwise 1–4.</summary>
    public int OrbitCount { get; set; }
    public float OrbitSpacing { get; set; } = 0.5f;
    public float RotationIntensity { get; set; } = 0.6f;
    public bool IdleRotation { get; set; } = true;
    public float IdleRotationSpeed { get; set; } = 0.35f;
    public float CardDepth { get; set; } = 0.6f;
    public float Perspective { get; set; } = 0.5f;
    public float Glow { get; set; } = 0.7f;
    public float AmbientLight { get; set; } = 0.6f;
    public bool Particles { get; set; } = true;
    public bool OrbitLines { get; set; } = true;
    public bool Parallax { get; set; } = true;
    public RotationDirection Direction { get; set; } = RotationDirection.Clockwise;

    internal void Normalize()
    {
        OrbitShape = FlowSwitchSettings.Clamp01(OrbitShape);
        OrbitCount = Math.Clamp(OrbitCount, 0, 4);
        OrbitSpacing = FlowSwitchSettings.Clamp01(OrbitSpacing);
        RotationIntensity = FlowSwitchSettings.Clamp01(RotationIntensity);
        IdleRotationSpeed = FlowSwitchSettings.Clamp01(IdleRotationSpeed);
        CardDepth = FlowSwitchSettings.Clamp01(CardDepth);
        Perspective = FlowSwitchSettings.Clamp01(Perspective);
        Glow = FlowSwitchSettings.Clamp01(Glow);
        AmbientLight = FlowSwitchSettings.Clamp01(AmbientLight);
    }
}

public sealed class AnimationSettings
{
    public AnimationPreset Preset { get; set; } = AnimationPreset.Smooth;
    /// <summary>Multiplier on every duration (Custom preset only).</summary>
    public float DurationScale { get; set; } = 1f;
    /// <summary>0 = soft, 1 = stiff (Custom preset only).</summary>
    public float SpringStiffness { get; set; } = 0.5f;
    /// <summary>Damping ratio; 1 = no overshoot (Custom preset only).</summary>
    public float SpringDamping { get; set; } = 0.9f;
    public float ScaleIntensity { get; set; } = 1f;
    public float RotationIntensity { get; set; } = 1f;
    public float DepthIntensity { get; set; } = 1f;
    public float Parallax { get; set; } = 0.5f;
    public bool MotionBlur { get; set; }
    public bool Breathing { get; set; } = true;
    public ReducedMotionMode ReducedMotion { get; set; } = ReducedMotionMode.FollowSystem;

    internal void Normalize()
    {
        DurationScale = FlowSwitchSettings.ClampF(DurationScale, 0.5f, 1.8f, 1f);
        SpringStiffness = FlowSwitchSettings.Clamp01(SpringStiffness);
        SpringDamping = FlowSwitchSettings.ClampF(SpringDamping, 0.6f, 1f, 0.9f);
        ScaleIntensity = FlowSwitchSettings.ClampF(ScaleIntensity, 0f, 1.5f, 1f);
        RotationIntensity = FlowSwitchSettings.ClampF(RotationIntensity, 0f, 1.5f, 1f);
        DepthIntensity = FlowSwitchSettings.ClampF(DepthIntensity, 0f, 1.5f, 1f);
        Parallax = FlowSwitchSettings.Clamp01(Parallax);
    }
}

public sealed class PerformanceSettings
{
    public QualityPreset Quality { get; set; } = QualityPreset.Auto;
    public bool LivePreviews { get; set; } = true;
    /// <summary>Starts capturing the desktop the moment Alt goes down, so the blur is ready when Tab arrives.</summary>
    public bool PrewarmOnAlt { get; set; } = true;
    /// <summary>Temporarily reduces effects if frames take too long.</summary>
    public bool AdaptiveQuality { get; set; } = true;
    public bool ShowFrameStats { get; set; }
}

public sealed class HotkeySettings
{
    public bool AltTab { get; set; } = true;
    public bool AltShiftTabReverse { get; set; } = true;
    /// <summary>Ctrl+Alt+Tab opens a switcher that stays open after Alt is released.</summary>
    public bool CtrlAltTabSticky { get; set; } = true;
    /// <summary>Alt+` cycles the windows of the current app.</summary>
    public bool AltBacktickSameApp { get; set; } = true;
    public bool CtrlWCloses { get; set; } = true;
    public bool DeleteCloses { get; set; } = true;
    public bool ArrowNavigation { get; set; } = true;
    public bool WheelNavigation { get; set; } = true;
}

public sealed class AdvancedSettings
{
    /// <summary>If the overlay does not respond within this time, native Alt+Tab is used instead.</summary>
    public int OverlayTimeoutMs { get; set; } = 150;
    public bool NativeInFullscreenGames { get; set; } = true;
    /// <summary>Process names (e.g. "game.exe") that keep the native Alt+Tab while they are in the foreground.</summary>
    public List<string> PassthroughProcesses { get; set; } = new();
    /// <summary>Process names whose windows are never shown in FlowSwitch.</summary>
    public List<string> HiddenProcesses { get; set; } = new();
    public bool AutoRestartAfterCrash { get; set; } = true;
    public bool RunElevated { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    internal void Normalize()
    {
        OverlayTimeoutMs = Math.Clamp(OverlayTimeoutMs, 80, 1000);
        PassthroughProcesses ??= new();
        HiddenProcesses ??= new();
    }
}
