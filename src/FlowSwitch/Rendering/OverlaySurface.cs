using System.Numerics;
using FlowSwitch.Core.Diagnostics;
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
        CreateHandle("FlowSwitch.Overlay",
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
            WS_POPUP, bounds.Left, bounds.Top, bounds.Width, bounds.Height, cursor: LoadCursorW(0, IDC_ARROW));

        // Keep the overlay out of every screen capture — including our own backdrop capture.
        if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Debug("WDA_EXCLUDEFROMCAPTURE unavailable (Windows 10 < 2004).");
        unsafe
        {
            int doNotRound = 1;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &doNotRound, sizeof(int));
        }

        CreateSwapChain(bounds.Width, bounds.Height);
    }

    public RECT Bounds { get; private set; }
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public bool IsVisible { get; private set; }
    public ID3D11RenderTargetView RenderTarget => _rtv!;

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
        _swapChain = _gfx.Factory.CreateSwapChainForComposition(_gfx.Device, desc, null);
        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swapChain2 is not null)
        {
            _swapChain2.MaximumFrameLatency = 1;
            FrameLatencyWaitable = _swapChain2.FrameLatencyWaitableObject;
        }

        _gfx.Composition.CreateTargetForHwnd(Handle, true, out _target).CheckError();
        _gfx.Composition.CreateVisual(out _visual).CheckError();
        _visual!.SetContent(_swapChain);
        _target!.SetRoot(_visual);
        _gfx.Composition.Commit().CheckError();
        CreateTargetView();
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
        SetWindowPos(Handle, HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        IsVisible = true;
    }

    public void Hide()
    {
        if (!IsVisible) return;
        ShowWindow(Handle, SW_HIDE);
        IsVisible = false;
        _tracking = false;
    }

    /// <summary>Presents the frame. <paramref name="vsync"/> = false for secondary monitors (never blocks).</summary>
    public bool Present(bool vsync)
    {
        var result = _swapChain!.Present(vsync ? 1u : 0u, PresentFlags.None);
        if (result.Failure)
        {
            Log.Warn($"Present failed: {result}");
            return false;
        }
        return true;
    }

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

    public override void Dispose()
    {
        _rtv?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();
        base.Dispose();
    }
}
