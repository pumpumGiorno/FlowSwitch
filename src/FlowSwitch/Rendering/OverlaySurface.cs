using System.Numerics;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Diagnostics;
using FlowSwitch.Interop;
using FlowSwitch.Shell;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static FlowSwitch.Interop.Win32;
using AlphaMode = Vortice.DXGI.AlphaMode;

namespace FlowSwitch.Rendering;

internal enum PointerEventKind
{
    Move,
    Leave,
    Down,
    Up,
    Wheel,
    RightUp,
}

internal readonly record struct PointerEvent(PointerEventKind Kind, Vector2 Position, int WheelDelta = 0);

/// <summary>
/// A full-monitor, topmost, never-activated window whose content is a premultiplied-alpha
/// DirectComposition swap chain. DWM composites it over the desktop with no redirection bitmap.
/// </summary>
/// <remarks>
/// The window never takes focus (WS_EX_NOACTIVATE / MA_NOACTIVATE): the foreground app stays
/// foreground while the user switches, exactly like the native switcher, and keyboard input keeps
/// flowing through the low-level hook. It is also excluded from screen capture, so the backdrop
/// capture never sees the overlay itself.
/// </remarks>
internal sealed class OverlaySurface : NativeWindow
{
    private readonly GraphicsDevice _gfx;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private ID3D11RenderTargetView? _rtv;
    private bool _tracking;

    public OverlaySurface(GraphicsDevice gfx, RECT bounds)
    {
        _gfx = gfx;
        Bounds = bounds;
        // Without DirectComposition the swap chain presents into the window itself, which then needs
        // its redirection surface.
        uint exStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (gfx.Composition is not null ? WS_EX_NOREDIRECTIONBITMAP : 0);
        try
        {
            CreateHandle("FlowSwitch.Overlay", exStyle, WS_POPUP, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                cursor: LoadCursorW(0, IDC_ARROW));
        }
        catch (Exception ex)
        {
            throw new GraphicsInitException("CreateWindowEx(overlay)", ex.Message, ex);
        }
        Log.Info($"Overlay HWND created: 0x{Handle:X}, bounds {bounds} ({bounds.Width}×{bounds.Height}), " +
                 $"exstyle 0x{GetExStyle(Handle):X8} (TOPMOST|TOOLWINDOW|NOACTIVATE{(gfx.Composition is not null ? "|NOREDIRECTIONBITMAP" : string.Empty)}), style 0x{GetStyle(Handle):X8}.");

        // Keep the overlay out of every screen capture — including our own backdrop capture.
        if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Info($"SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) unavailable: {ErrorText.Win32(System.Runtime.InteropServices.Marshal.GetLastPInvokeError())} (Windows 10 < 2004).");
        unsafe
        {
            int doNotRound = 1;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &doNotRound, sizeof(int));
        }

        try
        {
            CreateSwapChain(bounds.Width, bounds.Height);
        }
        catch
        {
            DisposeGraphics();
            base.Dispose();
            throw;
        }
    }

    public RECT Bounds { get; private set; }
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public bool IsVisible { get; private set; }
    public ID3D11RenderTargetView RenderTarget => _rtv!;

    /// <summary>True in the no-DirectComposition fallback: the window cannot be see-through.</summary>
    public bool IsOpaque { get; private set; }

    /// <summary>Signalled when the swap chain can accept a new frame (vsync pacing, lowest latency).</summary>
    public nint FrameLatencyWaitable { get; private set; }

    public event Action<PointerEvent>? Pointer;

    private void CreateSwapChain(int width, int height)
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        if (_gfx.Composition is { } composition)
        {
            _swapChain = GraphicsDevice.Stage("IDXGIFactory2.CreateSwapChainForComposition",
                () => _gfx.Factory.CreateSwapChainForComposition(_gfx.Device, desc, null));
        }
        else
        {
            desc.AlphaMode = AlphaMode.Ignore;
            _swapChain = GraphicsDevice.Stage("IDXGIFactory2.CreateSwapChainForHwnd",
                () => _gfx.Factory.CreateSwapChainForHwnd(_gfx.Device, Handle, desc, null, null));
            IsOpaque = true;
        }
        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swapChain2 is not null)
        {
            _swapChain2.MaximumFrameLatency = 1;
            FrameLatencyWaitable = _swapChain2.FrameLatencyWaitableObject;
        }
        Log.Info($"Swap chain created: {desc.Width}×{desc.Height} BGRA {(IsOpaque ? "opaque, for the window (fallback)" : "premultiplied, for composition")}, " +
                 $"flip-sequential, frame-latency waitable {(FrameLatencyWaitable != 0 ? "yes" : "no (IDXGISwapChain2 unavailable)")}.");

        if (_gfx.Composition is { } dcomp)
        {
            HostHealth.Composition = ComponentState.Failed; // until the whole chain below succeeds
            GraphicsDevice.Check("IDCompositionDevice.CreateTargetForHwnd", dcomp.CreateTargetForHwnd(Handle, true, out _target));
            GraphicsDevice.Check("IDCompositionDevice.CreateVisual", dcomp.CreateVisual(out _visual));
            GraphicsDevice.Check("IDCompositionVisual.SetContent", _visual!.SetContent(_swapChain));
            GraphicsDevice.Check("IDCompositionTarget.SetRoot", _target!.SetRoot(_visual));
            GraphicsDevice.Check("IDCompositionDevice.Commit", dcomp.Commit());
            Log.Info("DirectComposition initialized (target → visual → swap chain, committed).");
            HostHealth.Composition = ComponentState.Ready;
        }
        GraphicsDevice.Stage("CreateRenderTargetView(back buffer)", () =>
        {
            CreateTargetView();
            return true;
        });
    }

    private void CreateTargetView()
    {
        _rtv?.Dispose();
        using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _gfx.Device.CreateRenderTargetView(buffer, null);
    }

    /// <summary>Moves / resizes to new monitor bounds (resolution or layout change).</summary>
    public void SetBounds(RECT bounds)
    {
        if (bounds.Left == Bounds.Left && bounds.Top == Bounds.Top && bounds.Width == Bounds.Width && bounds.Height == Bounds.Height) return;
        Bounds = bounds;
        _gfx.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        _rtv?.Dispose();
        _rtv = null;
        _gfx.Context.Flush();
        _swapChain!.ResizeBuffers(2, (uint)bounds.Width, (uint)bounds.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        CreateTargetView();
        SetWindowPos(Handle, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE);
    }

    public void Show()
    {
        if (IsVisible) return;
        bool ok = SetWindowPos(Handle, HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        IsVisible = true;
        if (!ok)
        {
            ErrorText.LogWin32Failure("SetWindowPos(overlay, HWND_TOPMOST, SWP_SHOWWINDOW)", false, error);
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }
        LogPlacement("shown");
    }

    /// <summary>Logs where the window really is: position, size, visibility, topmost, foreground.</summary>
    public string LogPlacement(string what)
    {
        GetWindowRect(Handle, out RECT actual);
        uint ex = GetExStyle(Handle);
        bool visible = IsWindowVisible(Handle);
        nint foreground = GetForegroundWindow();
        string text = $"Overlay {what}: HWND 0x{Handle:X}, visible {visible}, rect {actual} ({actual.Width}×{actual.Height}), " +
                      $"expected {Bounds}, topmost {(ex & WS_EX_TOPMOST) != 0}, cloaked {IsCloaked(Handle)}, foreground 0x{foreground:X}.";
        if (!visible || actual.Width != Bounds.Width || actual.Height != Bounds.Height || (ex & WS_EX_TOPMOST) == 0) Log.Warn(text);
        else Log.Info(text);
        return text;
    }

    public void Hide()
    {
        if (!IsVisible) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        ShowWindow(Handle, SW_HIDE);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms > 50) Log.Warn($"Hiding the overlay took {ms:0} ms.");
        else Log.Debug($"Overlay hidden ({ms:0.0} ms).");
        IsVisible = false;
        _tracking = false;
    }

    /// <summary>Presents the frame. <paramref name="vsync"/> = false for secondary monitors (never blocks).</summary>
    public bool Present(bool vsync)
    {
        var result = _swapChain!.Present(vsync ? 1u : 0u, PresentFlags.None);
        if (result.Failure)
        {
            LastPresentError = ErrorText.HResult(result.Code);
            Log.Warn($"IDXGISwapChain.Present failed: {LastPresentError}");
            return false;
        }
        return true;
    }

    public string? LastPresentError { get; private set; }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam, out bool handled)
    {
        handled = true;
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_NCHITTEST:
                return HTCLIENT;
            case WM_ERASEBKGND:
                return 1;
            case WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _tracking = TrackMouseEvent(ref tme);
                }
                Pointer?.Invoke(new PointerEvent(PointerEventKind.Move, Point(lParam)));
                return 0;
            case WM_MOUSELEAVE:
                _tracking = false;
                Pointer?.Invoke(new PointerEvent(PointerEventKind.Leave, default));
                return 0;
            case WM_LBUTTONDOWN:
                Pointer?.Invoke(new PointerEvent(PointerEventKind.Down, Point(lParam)));
                return 0;
            case WM_LBUTTONUP:
                Pointer?.Invoke(new PointerEvent(PointerEventKind.Up, Point(lParam)));
                return 0;
            case WM_RBUTTONUP:
                Pointer?.Invoke(new PointerEvent(PointerEventKind.RightUp, Point(lParam)));
                return 0;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                // Wheel coordinates are in screen space.
                var screen = Point(lParam);
                int delta = (short)((wParam >> 16) & 0xFFFF);
                if (msg == WM_MOUSEHWHEEL) delta = -delta;
                Pointer?.Invoke(new PointerEvent(PointerEventKind.Wheel, screen - new Vector2(Bounds.Left, Bounds.Top), delta));
                return 0;
            default:
                handled = false;
                return 0;
        }
    }

    private static Vector2 Point(nint lParam) => new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    private void DisposeGraphics()
    {
        _rtv?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();
        _rtv = null;
        _visual = null;
        _target = null;
        _swapChain2 = null;
        _swapChain = null;
    }

    public override void Dispose()
    {
        DisposeGraphics();
        base.Dispose();
    }
}
