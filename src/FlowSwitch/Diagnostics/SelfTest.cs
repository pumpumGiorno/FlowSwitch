using System.Diagnostics;
using System.Runtime.InteropServices;
using FlowSwitch.Capture;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Input;
using FlowSwitch.Interop;
using FlowSwitch.Overlay;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Diagnostics;

/// <summary>Values the switcher-thread part of the self-test hands to the background part.</summary>
internal sealed class SelfTestContext
{
    public object? WinRtDevice;
    public nint Monitor;
}

/// <summary>
/// Settings → Diagnostics → Run diagnostics. Checks every link of the chain
/// Keyboard → Hook → Controller → Overlay → Renderer → Present on the real system and writes a
/// PASS/FAIL report to %LOCALAPPDATA%\FlowSwitch\Logs\diagnostics.txt (and the log).
/// </summary>
internal static class SelfTest
{
    private static int s_running;

    public static void Start(int requestId, HookBridge bridge, SwitcherThread switcher, string versionLine)
    {
        if (Interlocked.Exchange(ref s_running, 1) == 1)
        {
            Log.Info("Self-test already running; request ignored.");
            return;
        }
        var thread = new Thread(() =>
        {
            try
            {
                Run(requestId, bridge, switcher, versionLine);
            }
            catch (Exception ex)
            {
                Log.Error("Self-test crashed", ex);
                WriteReport(requestId, $"FlowSwitch Diagnostics\n\nThe self-test itself failed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref s_running, 0);
            }
        })
        { Name = "FlowSwitch.SelfTest", IsBackground = true };
        thread.Start();
    }

    private static void Run(int requestId, HookBridge bridge, SwitcherThread switcher, string versionLine)
    {
        Log.Info($"Self-test {requestId} started.");
        var report = new SelfTestReport();
        var uptime = DateTimeOffset.Now - HostHealth.StartedAt;

        report.Add("Host process", CheckResult.Pass,
            $"FlowSwitch.exe pid {Environment.ProcessId}, running {uptime:hh\\:mm\\:ss}, {Environment.ProcessPath}");
        report.Add("Settings IPC", CheckResult.Pass, "request received through the host window message");
        report.Add("Elevated", CheckResult.Info, HostHealth.Elevated
            ? "Yes — also works over apps running as administrator"
            : "No — works everywhere except while an app running as administrator is in front (Windows UIPI)");

        if (HostHealth.SafeMode)
            report.Add("Safe mode", CheckResult.Fail, $"ON — keyboard hook disabled. {HostHealth.SafeModeReason} Use \"Exit safe mode\".");
        else
            report.Add("Safe mode", CheckResult.Pass, "off");
        if (!HostHealth.Enabled) report.Add("Enabled", CheckResult.Warn, "FlowSwitch is turned off in Settings → General — Windows Alt+Tab is used.");
        if (HostHealth.Paused) report.Add("Paused", CheckResult.Warn, "FlowSwitch is paused from the tray icon.");

        long events = Interlocked.Read(ref HostHealth.HookEvents);
        report.Add("Keyboard hook", HostHealth.Hook == ComponentState.Ready ? CheckResult.Pass : CheckResult.Fail,
            HostHealth.Hook == ComponentState.Ready
                ? $"WH_KEYBOARD_LL handle 0x{HostHealth.HookHandle:X} on thread {HostHealth.HookThreadId}; {events} key events seen" +
                  (HostHealth.LastTabDownTicks > 0 ? $"; last Alt+Tab-context Tab {(Environment.TickCount64 - HostHealth.LastTabDownTicks) / 1000.0:0.0} s ago" : string.Empty)
                : HostHealth.HookError ?? $"state {HostHealth.Hook}");

        // The switcher-thread checks (enumeration, D3D11, DirectComposition, shaders, overlay, test frame).
        var context = new SelfTestContext();
        var done = new ManualResetEventSlim(false);
        if (HostHealth.Switcher != ComponentState.Ready)
        {
            report.Add("Switcher thread", CheckResult.Fail, HostHealth.SwitcherError ?? "not running");
        }
        else
        {
            switcher.Post(c =>
            {
                try
                {
                    c.RunSelfTest(report, context);
                }
                finally
                {
                    done.Set();
                }
            });
            if (!done.Wait(TimeSpan.FromSeconds(15)))
                report.Add("Switcher thread", CheckResult.Fail, "did not respond within 15 s (hung?)");
        }

        // Windows.Graphics.Capture: one real frame of the primary monitor.
        if (context.WinRtDevice is null)
        {
            report.Add("Windows Graphics Capture", CheckResult.Fail, HostHealth.CaptureError ?? "no WinRT device (renderer not ready)");
        }
        else
        {
            bool ok = CaptureProbe.TryCaptureFrame(context.WinRtDevice, context.Monitor, TimeSpan.FromSeconds(2), out string detail);
            report.Add("Windows Graphics Capture", ok ? CheckResult.Pass : CheckResult.Fail, detail + (ok ? string.Empty : " — the switcher still works, without live previews"));
        }

        TestInterception(report, bridge);

        string windows = $"{Environment.OSVersion.VersionString} ({SystemDescription.WindowsDisplayVersion()}), {RuntimeInformation.ProcessArchitecture}";
        string text = report.Render($"FlowSwitch Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{versionLine}\n{windows}\nAdapter: {HostHealth.Adapter ?? "?"}");
        WriteReport(requestId, text);
        Log.Info("Self-test finished:\n" + text);
    }

    /// <summary>
    /// End to end: injects a synthetic Alt+Tab (marked so the hook treats it like a real one) and
    /// checks that it reaches the switcher, then cancels with Esc and releases Alt.
    /// </summary>
    private static unsafe void TestInterception(SelfTestReport report, HookBridge bridge)
    {
        const string name = "Alt+Tab interception";
        if (!HostHealth.InterceptionActive)
        {
            string reason = HostHealth.SafeMode ? "safe mode is on"
                : !HostHealth.Enabled ? "FlowSwitch is turned off"
                : HostHealth.Paused ? "FlowSwitch is paused"
                : HostHealth.Hook != ComponentState.Ready ? "keyboard hook not installed"
                : "renderer / overlay not ready";
            report.Add(name, CheckResult.Fail, $"not active: {reason} — Windows Alt+Tab is used");
            return;
        }
        if (IsKeyDown(VK_MENU) || IsKeyDown(VK_TAB) || IsKeyDown(VK_CONTROL) || IsKeyDown(VK_SHIFT) || bridge.IsSessionActive)
        {
            report.Add(name, CheckResult.Skip, "keys are held or a switcher session is open; run again without touching the keyboard");
            return;
        }

        long before = Interlocked.Read(ref HostHealth.InterceptedSessions);
        long fallbacksBefore = Interlocked.Read(ref HostHealth.NativeFallbacks);
        var sw = Stopwatch.StartNew();
        INPUT* down = stackalloc INPUT[3];
        INPUT* up = stackalloc INPUT[3];
        down[0] = INPUT.Key(VK_LMENU, false, HookBridge.DiagnosticMarker);
        down[1] = INPUT.Key(VK_TAB, false, HookBridge.DiagnosticMarker);
        down[2] = INPUT.Key(VK_TAB, true, HookBridge.DiagnosticMarker);
        uint sent = SendInput(3, down, sizeof(INPUT));
        int sendError = Marshal.GetLastPInvokeError();
        bool intercepted = false, acknowledged = false;
        int session = 0;
        try
        {
            if (sent != 3)
            {
                report.Add(name, CheckResult.Skip, $"SendInput blocked ({ErrorText.Win32(sendError)}) — e.g. an administrator window is in front");
                return;
            }
            while (sw.ElapsedMilliseconds < 1000)
            {
                if (!intercepted && Interlocked.Read(ref HostHealth.InterceptedSessions) > before)
                {
                    intercepted = true;
                    session = bridge.SessionId;
                }
                if (intercepted && bridge.IsAcknowledged(session))
                {
                    acknowledged = true;
                    break;
                }
                Thread.Sleep(5);
            }
            // Let the switcher get to its first frame, like a real (short) Alt+Tab.
            if (acknowledged) Thread.Sleep(150);
        }
        finally
        {
            up[0] = INPUT.Key(VK_ESCAPE, false, HookBridge.DiagnosticMarker);
            up[1] = INPUT.Key(VK_ESCAPE, true, HookBridge.DiagnosticMarker);
            up[2] = INPUT.Key(VK_LMENU, true, HookBridge.DiagnosticMarker);
            SendInput(3, up, sizeof(INPUT));
        }

        if (acknowledged)
            report.Add(name, CheckResult.Pass, $"synthetic Alt+Tab → hook → switcher session {session} acknowledged in {sw.ElapsedMilliseconds - 150} ms, then cancelled with Esc");
        else if (intercepted)
            report.Add(name, CheckResult.Fail, $"hook intercepted it (session {session}) but the switcher did not acknowledge within 1 s" +
                (Interlocked.Read(ref HostHealth.NativeFallbacks) > fallbacksBefore ? " — handed to Windows' Alt+Tab" : string.Empty));
        else
            report.Add(name, CheckResult.Fail, $"the hook did not take the synthetic Alt+Tab (last reason: {HostHealth.LastPassReason ?? "none logged"}; " +
                $"key events seen: {Interlocked.Read(ref HostHealth.HookEvents)})");
    }

    private static void WriteReport(int requestId, string text)
    {
        try
        {
            string path = FlowSwitchPaths.DiagnosticsReport;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, $"Request: {requestId}\n{text}", new System.Text.UTF8Encoding(true));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write the diagnostics report", ex);
        }
    }
}
