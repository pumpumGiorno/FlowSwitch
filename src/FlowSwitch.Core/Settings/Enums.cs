namespace FlowSwitch.Core.Settings;

public enum SwitcherMode
{
    SolarSystem,
    OrbitMinimal,
    Carousel,
    Grid,
    CoverFlow,
}

public enum ThemeMode
{
    Dark,
    Oled,
    /// <summary>Adapts background dimming to the brightness of the desktop behind the overlay.</summary>
    Auto,
}

public enum MonitorPlacement
{
    /// <summary>Monitor that contains the currently active window (default).</summary>
    ActiveWindow,
    MouseCursor,
    AllMonitors,
}

public enum GroupingMode
{
    Off,
    /// <summary>Groups an app once it has three or more windows. The two most recent windows always stay separate.</summary>
    Auto,
    Always,
}

public enum AnimationPreset
{
    Smooth,
    Snappy,
    Cinematic,
    Minimal,
    Custom,
}

public enum QualityPreset
{
    /// <summary>High on AC power, Balanced on battery, Battery Saver when Windows battery saver is on.</summary>
    Auto,
    BatterySaver,
    Balanced,
    High,
    Ultra,
}

public enum ReducedMotionMode
{
    FollowSystem,
    On,
    Off,
}

public enum RotationDirection
{
    Clockwise,
    CounterClockwise,
}

public enum AmbientColorSource
{
    FromApp,
    Custom,
    Neutral,
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}
