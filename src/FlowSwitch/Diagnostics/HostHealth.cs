using FlowSwitch.Core.Ipc;

namespace FlowSwitch.Diagnostics;

internal enum ComponentState
{
    NotStarted,
    Starting,
    Ready,
    Failed,
    Unavailable,
}

/// <summary>
/// What is actually working inside this process right now. Written by the thread that owns each
/// component, read by the Settings status query and the self-test. Plain volatile fields: readers
/// only ever need a recent snapshot.
/// </summary>
internal static class HostHealth
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;

    // Keyboard hook (hook thread).
    public static volatile ComponentState Hook = ComponentState.NotStarted;
    public static volatile string? HookError;
    public static long HookHandle;
    public static uint HookThreadId;
    public static long HookEvents;
    public static long LastAltDownTicks;
    public static long LastTabDownTicks;
    public static long InterceptedSessions;
    public static long NativeFallbacks;
    public static volatile string? LastPassReason;

    // Switcher thread, renderer, overlay, capture (switcher thread).
    public static volatile ComponentState Switcher = ComponentState.NotStarted;
    public static volatile string? SwitcherError;
    public static volatile ComponentState Renderer = ComponentState.NotStarted;
    public static volatile string? RendererError;
    public static volatile string? RendererStage;
    public static volatile string? Adapter;
    public static volatile ComponentState D3D = ComponentState.NotStarted;
    public static volatile ComponentState Composition = ComponentState.NotStarted;
    public static volatile ComponentState Overlay = ComponentState.NotStarted;
    public static volatile string? OverlayError;
    public static long OverlayHwnd;
    public static volatile ComponentState Capture = ComponentState.NotStarted;
    public static volatile string? CaptureError;
    public static volatile ComponentState Shaders = ComponentState.NotStarted;
    public static volatile string? ShaderError;
    public static volatile bool FirstFrameRendered;
    public static volatile string? LastFrameError;

    // Process-wide.
    public static volatile bool Elevated;
    public static volatile bool SafeMode;
    public static volatile string? SafeModeReason;
    public static volatile bool Enabled;
    public static volatile bool Paused;
    public static volatile bool DiagnosticHotkey;
    public static volatile string? WinRtProjection;
    public static volatile bool WinRtProjectionBroken;

    /// <summary>Alt+Tab is taken over right now: on, not paused, not in safe mode, hook and renderer both working.</summary>
    public static bool InterceptionActive =>
        Enabled && !Paused && !SafeMode && Hook == ComponentState.Ready && Renderer == ComponentState.Ready && Overlay == ComponentState.Ready;

    public static HostStatus ToStatus()
    {
        var s = HostStatus.Valid;
        if (Hook == ComponentState.Ready) s |= HostStatus.HookInstalled;
        if (Hook == ComponentState.Failed) s |= HostStatus.HookFailed;
        if (Interlocked.Read(ref HookEvents) > 0) s |= HostStatus.HookReceivingInput;
        if (Switcher == ComponentState.Ready) s |= HostStatus.SwitcherRunning;
        if (Renderer == ComponentState.Ready) s |= HostStatus.RendererReady;
        if (Renderer == ComponentState.Failed) s |= HostStatus.RendererFailed;
        if (Renderer == ComponentState.Starting) s |= HostStatus.RendererInitializing;
        if (Overlay == ComponentState.Ready) s |= HostStatus.OverlayReady;
        if (Capture == ComponentState.Ready) s |= HostStatus.CaptureReady;
        if (Capture is ComponentState.Failed or ComponentState.Unavailable) s |= HostStatus.CaptureFailed;
        if (Elevated) s |= HostStatus.Elevated;
        if (SafeMode) s |= HostStatus.SafeMode;
        if (Enabled) s |= HostStatus.Enabled;
        if (Paused) s |= HostStatus.Paused;
        if (InterceptionActive) s |= HostStatus.InterceptionActive;
        if (Interlocked.Read(ref InterceptedSessions) > 0) s |= HostStatus.AltTabSeen;
        if (DiagnosticHotkey) s |= HostStatus.DiagnosticHotkey;
        if (WinRtProjectionBroken) s |= HostStatus.WinRtProjectionBroken;
        return s;
    }
}
