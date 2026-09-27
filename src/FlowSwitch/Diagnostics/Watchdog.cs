using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Input;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Diagnostics;

/// <summary>
/// Independent thread that watches the switcher (UI) thread. A frozen full-screen topmost overlay
/// would be the worst possible failure, so if the overlay is visible and the UI stops rendering
/// for 2.5 s the process restarts itself (Windows then removes the hook and the overlay instantly).
/// While idle, the UI thread is probed every few seconds; three unanswered probes also restart.
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private const int OverlayFrozenMs = 2500;
    private const int ProbeIntervalMs = 10_000;
    private const uint ProbeTimeoutMs = 3000;

    private readonly HookBridge _bridge;
    private readonly Func<bool> _overlayVisible;
    private readonly Func<nint> _switcherWindow;
    private readonly CrashGuard _crash;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();

    public Watchdog(HookBridge bridge, Func<bool> overlayVisible, Func<nint> switcherWindow, CrashGuard crash)
    {
        _bridge = bridge;
        _overlayVisible = overlayVisible;
        _switcherWindow = switcherWindow;
        _crash = crash;
        _thread = new Thread(Run) { Name = "FlowSwitch.Watchdog", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        int failedProbes = 0;
        long lastProbe = Environment.TickCount64;
        while (!_stop.IsCancellationRequested)
        {
            if (_stop.Token.WaitHandle.WaitOne(250)) break;
            try
            {
                if (_overlayVisible())
                {
                    long silent = _bridge.MillisecondsSinceHeartbeat;
                    if (silent > OverlayFrozenMs)
                    {
                        _crash.OnHang($"overlay visible but no frame for {silent} ms");
                        return;
                    }
                    failedProbes = 0;
                    continue;
                }

                if (Environment.TickCount64 - lastProbe < ProbeIntervalMs) continue;
                lastProbe = Environment.TickCount64;
                nint target = _switcherWindow();
                if (target == 0) continue;
                nint ok = SendMessageTimeoutW(target, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, ProbeTimeoutMs, out _);
                if (ok != 0)
                {
                    failedProbes = 0;
                    continue;
                }
                failedProbes++;
                Log.Warn($"Switcher thread did not answer probe ({failedProbes}/3, error {Marshal.GetLastPInvokeError()}).");
                if (failedProbes >= 3)
                {
                    _crash.OnHang("switcher thread unresponsive");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Watchdog", ex);
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
