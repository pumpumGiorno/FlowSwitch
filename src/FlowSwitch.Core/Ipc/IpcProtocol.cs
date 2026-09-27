namespace FlowSwitch.Core.Ipc;

/// <summary>
/// How the Settings app (and a second launch of FlowSwitch.exe) talks to the running instance:
/// it finds the hidden host window by class name and posts the registered message with a command.
/// Settings themselves travel through settings.json, which the resident process watches.
/// </summary>
public static class IpcProtocol
{
    public const string HostWindowClass = "FlowSwitch.Host";
    public const string CommandMessage = "FlowSwitch.Command";
    public const string InstanceMutex = @"Local\FlowSwitch.Instance";
    public const string SettingsInstanceMutex = @"Local\FlowSwitch.Settings.Instance";

    public const int OpenSettings = 1;
    public const int ShowPreview = 2;
    public const int ReloadSettings = 3;
    public const int Exit = 4;
    public const int TogglePause = 5;
    public const int Restart = 6;
    public const int OnboardingFinished = 7;

    /// <summary>
    /// Sent (not posted) with SendMessageTimeout: the host answers synchronously with a
    /// <see cref="HostStatus"/> bit set in the message result.
    /// </summary>
    public const int QueryStatus = 8;

    /// <summary>Runs the self-test. lParam = request id; the report is written to <see cref="FlowSwitchPaths.DiagnosticsReport"/>.</summary>
    public const int RunDiagnostics = 9;

    /// <summary>Shows the switcher with the real open windows for a few seconds, without Alt+Tab.</summary>
    public const int TestOverlay = 10;

    /// <summary>Leaves safe mode and turns FlowSwitch on again.</summary>
    public const int ExitSafeMode = 11;
}

/// <summary>Health bits returned by <see cref="IpcProtocol.QueryStatus"/>.</summary>
[Flags]
public enum HostStatus : long
{
    None = 0,
    /// <summary>Always set in a real answer (distinguishes "no bits" from "no answer").</summary>
    Valid = 1 << 0,
    HookInstalled = 1 << 1,
    HookFailed = 1 << 2,
    /// <summary>The hook has received at least one keyboard event since it was installed.</summary>
    HookReceivingInput = 1 << 3,
    SwitcherRunning = 1 << 4,
    RendererReady = 1 << 5,
    RendererFailed = 1 << 6,
    RendererInitializing = 1 << 7,
    OverlayReady = 1 << 8,
    CaptureReady = 1 << 9,
    CaptureFailed = 1 << 10,
    Elevated = 1 << 11,
    SafeMode = 1 << 12,
    Enabled = 1 << 13,
    Paused = 1 << 14,
    /// <summary>Alt+Tab is currently taken over (enabled, not paused, hook and renderer ready).</summary>
    InterceptionActive = 1 << 15,
    /// <summary>At least one Alt+Tab was intercepted since start.</summary>
    AltTabSeen = 1 << 16,
    DiagnosticHotkey = 1 << 17,
    WinRtProjectionBroken = 1 << 18,
}

/// <summary>File locations shared by FlowSwitch.exe and FlowSwitch.Settings.exe.</summary>
public static class FlowSwitchPaths
{
    public static string LocalData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowSwitch");

    /// <summary>%LOCALAPPDATA%\FlowSwitch\Logs</summary>
    public static string LogDirectory => Path.Combine(LocalData, "Logs");

    /// <summary>%LOCALAPPDATA%\FlowSwitch\Logs\flowswitch.log — the resident app's log.</summary>
    public static string HostLog => Path.Combine(LogDirectory, "flowswitch.log");

    /// <summary>%LOCALAPPDATA%\FlowSwitch\Logs\settings.log — the Settings app's log.</summary>
    public static string SettingsLog => Path.Combine(LogDirectory, "settings.log");

    /// <summary>Latest self-test report (first line: "Request: &lt;id&gt;").</summary>
    public static string DiagnosticsReport => Path.Combine(LogDirectory, "diagnostics.txt");

    public static string ShaderCache => Path.Combine(LocalData, "ShaderCache");
}
