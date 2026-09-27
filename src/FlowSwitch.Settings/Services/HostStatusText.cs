using FlowSwitch.Core.Ipc;

namespace FlowSwitch.Settings.Services;

public enum StatusTone
{
    Good,
    Warning,
    Error,
    Neutral,
}

/// <summary>Turns the host's health bits into the words Settings shows. Nothing here guesses: no answer means "Not running".</summary>
public static class HostStatusText
{
    /// <summary>The big status line: "FlowSwitch: Running" and what it means for Alt+Tab.</summary>
    public static (string Headline, string Detail, StatusTone Tone) Summary(HostStatus? status, string? startError)
    {
        if (status is not { } s)
        {
            return ("FlowSwitch: Not running",
                startError ?? "The resident app is not running — Windows' own Alt + Tab is active.", StatusTone.Error);
        }
        if (s.HasFlag(HostStatus.SafeMode))
            return ("FlowSwitch: Safe mode", "It stopped repeatedly and paused itself. Windows' Alt + Tab is active. Use \"Exit safe mode\" on the Diagnostics page.", StatusTone.Warning);
        if (!s.HasFlag(HostStatus.Enabled))
            return ("FlowSwitch: Turned off", "Running, but switched off in General. Windows' Alt + Tab is active.", StatusTone.Warning);
        if (s.HasFlag(HostStatus.Paused))
            return ("FlowSwitch: Paused", "Paused from the tray icon. Windows' Alt + Tab is active.", StatusTone.Warning);
        if (s.HasFlag(HostStatus.HookFailed) || !s.HasFlag(HostStatus.HookInstalled))
            return ("FlowSwitch: Keyboard hook failed", "Alt + Tab cannot be taken over — Windows' Alt + Tab is active. See Diagnostics.", StatusTone.Error);
        if (s.HasFlag(HostStatus.RendererInitializing))
            return ("FlowSwitch: Starting…", "Preparing the renderer. Windows' Alt + Tab works meanwhile.", StatusTone.Neutral);
        if (!s.HasFlag(HostStatus.RendererReady) || !s.HasFlag(HostStatus.OverlayReady))
            return ("FlowSwitch: Renderer failed", "The overlay cannot be drawn — Windows' Alt + Tab is active. Run diagnostics for the exact error.", StatusTone.Error);
        if (!s.HasFlag(HostStatus.SwitcherRunning))
            return ("FlowSwitch: Switcher stopped", "Windows' Alt + Tab is active. Restart FlowSwitch.", StatusTone.Error);
        return ("FlowSwitch: Running", "Alt + Tab is handled by FlowSwitch.", StatusTone.Good);
    }

    /// <summary>The diagnostic block rows.</summary>
    public static IReadOnlyList<(string Name, string Value, StatusTone Tone)> Rows(HostStatus? status)
    {
        if (status is not { } s)
        {
            return new[]
            {
                ("Main process", "Not running", StatusTone.Error),
                ("Keyboard hook", "—", StatusTone.Neutral),
                ("Renderer", "—", StatusTone.Neutral),
                ("Overlay", "—", StatusTone.Neutral),
                ("Graphics Capture", "—", StatusTone.Neutral),
                ("Elevated", "—", StatusTone.Neutral),
                ("Safe Mode", "—", StatusTone.Neutral),
            };
        }
        return new[]
        {
            ("Main process", "Running", StatusTone.Good),
            ("Keyboard hook",
                s.HasFlag(HostStatus.HookInstalled) ? s.HasFlag(HostStatus.HookReceivingInput) ? "Active" : "Active (no keys seen yet)"
                : s.HasFlag(HostStatus.HookFailed) ? "Failed" : "Not installed",
                s.HasFlag(HostStatus.HookInstalled) ? StatusTone.Good : StatusTone.Error),
            ("Renderer",
                s.HasFlag(HostStatus.RendererReady) ? "Ready" : s.HasFlag(HostStatus.RendererInitializing) ? "Starting" : s.HasFlag(HostStatus.RendererFailed) ? "Failed" : "Not started",
                s.HasFlag(HostStatus.RendererReady) ? StatusTone.Good : s.HasFlag(HostStatus.RendererInitializing) ? StatusTone.Neutral : StatusTone.Error),
            ("Overlay", s.HasFlag(HostStatus.OverlayReady) ? "Ready" : "Failed",
                s.HasFlag(HostStatus.OverlayReady) ? StatusTone.Good : StatusTone.Error),
            ("Graphics Capture",
                s.HasFlag(HostStatus.CaptureReady) ? "Ready" : s.HasFlag(HostStatus.CaptureFailed) ? "Failed (no live previews)" : "Not started",
                s.HasFlag(HostStatus.CaptureReady) ? StatusTone.Good : StatusTone.Warning),
            ("Elevated", s.HasFlag(HostStatus.Elevated) ? "Yes" : "No", StatusTone.Neutral),
            ("Safe Mode", s.HasFlag(HostStatus.SafeMode) ? "ON" : "OFF", s.HasFlag(HostStatus.SafeMode) ? StatusTone.Warning : StatusTone.Good),
            ("Alt + Tab", s.HasFlag(HostStatus.InterceptionActive) ? "Handled by FlowSwitch" : "Windows (native)",
                s.HasFlag(HostStatus.InterceptionActive) ? StatusTone.Good : StatusTone.Warning),
        };
    }
}
