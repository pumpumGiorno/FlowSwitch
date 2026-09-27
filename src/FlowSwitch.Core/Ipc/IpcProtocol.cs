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
}
