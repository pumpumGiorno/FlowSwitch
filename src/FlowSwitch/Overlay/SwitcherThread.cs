using System.Collections.Concurrent;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Settings;
using FlowSwitch.Diagnostics;
using FlowSwitch.Input;
using FlowSwitch.Shell;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Overlay;

/// <summary>
/// The thread that owns everything visual: overlay windows, the Direct3D device, WinEvent hooks
/// and the render loop.
/// </summary>
/// <remarks>
/// Idle: blocks in MsgWaitForMultipleObjectsEx — zero CPU, zero GPU.
/// Active: waits on the swap chain's frame-latency object, so frames are paced by the display
/// itself (60, 120, 144, 165, 240 Hz …) with one frame of latency and no artificial cap.
/// Every loop iteration reports a heartbeat to the keyboard hook.
/// </remarks>
internal sealed unsafe class SwitcherThread : IDisposable
{
    private const uint WmInvoke = WM_APP + 50;

    private readonly HookBridge _bridge;
    private readonly string _shaderCache;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private HostWindow? _window;
    private SwitcherController? _controller;
    private volatile bool _running;

    public SwitcherThread(HookBridge bridge, string shaderCache)
    {
        _bridge = bridge;
        _shaderCache = shaderCache;
    }

    public nint WindowHandle => _window?.Handle ?? 0;

    public bool OverlayVisible => _controller?.OverlayVisible ?? false;

    public void Start(FlowSwitchSettings settings)
    {
        _running = true;
        _thread = new Thread(() => Run(settings)) { Name = "FlowSwitch.Switcher", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(10))) Log.Error("Switcher thread did not start.");
    }

    /// <summary>Runs <paramref name="action"/> on the switcher thread.</summary>
    public void Post(Action<SwitcherController> action)
    {
        _queue.Enqueue(() =>
        {
            if (_controller is not null) action(_controller);
        });
        if (_window is { Handle: not 0 } w) PostMessageW(w.Handle, WmInvoke, 0, 0);
    }

    private void Run(FlowSwitchSettings settings)
    {
        HostHealth.Switcher = ComponentState.Starting;
        try
        {
            Log.Info("Switcher thread started.");
            _window = new HostWindow(this);
            _controller = new SwitcherController(_bridge, _shaderCache);
            _controller.WakeRequested += () => Post(c => c.PumpIdle());
            _controller.ApplySettings(settings);
            _bridge.TargetWindow = _window.Handle;
            HostHealth.Switcher = ComponentState.Ready;
            Log.Info($"Switcher ready (message window 0x{_window.Handle:X}).");
        }
        catch (Exception ex)
        {
            HostHealth.Switcher = ComponentState.Failed;
            HostHealth.SwitcherError = ErrorText.Of(ex);
            Log.Error("Switcher thread failed to start — Alt+Tab stays with Windows", ex);
            _started.Set();
            return;
        }
        _started.Set();

        // Graphics come up right away: until they do, the hook leaves Alt+Tab to Windows.
        Post(c => c.InitializeGraphics());

        while (_running)
        {
            _bridge.ReportAlive();
            var controller = _controller;
            if (controller.WantsFrames)
            {
                nint waitable = controller.FrameWaitable;
                uint result = waitable != 0
                    ? MsgWaitForMultipleObjectsEx(1, &waitable, 34, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
                    : MsgWaitForMultipleObjectsEx(0, null, 4, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                bool frameReady = waitable == 0 || result == WAIT_OBJECT_0 || result == WAIT_TIMEOUT;
                Pump();
                if (!_running) break;
                if (frameReady) controller.RenderFrame();
            }
            else
            {
                // Idle: no CPU, no GPU — except to retry a failed renderer on schedule.
                uint timeout = controller.SecondsUntilGraphicsRetry is { } retry ? (uint)Math.Clamp(retry * 1000 + 10, 10, int.MaxValue) : INFINITE;
                MsgWaitForMultipleObjectsEx(0, null, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                Pump();
                if (!_running) break;
                controller.RetryGraphicsIfDue();
            }
        }

        Log.Info("Switcher thread exiting.");
        _bridge.TargetWindow = 0;
        _bridge.RendererReady = false;
        _controller.Dispose();
        _window.Dispose();
    }

    private void Pump()
    {
        while (PeekMessageW(out MSG msg, 0, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
            {
                _running = false;
                return;
            }
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Switcher action failed", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_thread is null) return;
        _running = false;
        if (_window is { Handle: not 0 } w) PostMessageW(w.Handle, WM_NULL, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    /// <summary>Message-only window receiving keyboard-hook messages on the switcher thread.</summary>
    private sealed class HostWindow : NativeWindow
    {
        private readonly SwitcherThread _owner;

        public HostWindow(SwitcherThread owner)
        {
            _owner = owner;
            CreateHandle("FlowSwitch.Switcher", 0, 0, 0, 0, 0, 0, parent: HWND_MESSAGE);
        }

        protected override nint WndProc(uint msg, nuint wParam, nint lParam, out bool handled)
        {
            handled = true;
            if (msg is >= HookMessages.Begin and <= HookMessages.Ping)
            {
                _owner._controller?.OnHookMessage(msg, wParam, lParam);
                return 0;
            }
            if (msg == WmInvoke) return 0; // queue drained by the loop
            handled = false;
            return 0;
        }
    }
}
