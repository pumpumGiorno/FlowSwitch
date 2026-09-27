using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace FlowSwitch.Capture;

/// <summary>One live Windows.Graphics.Capture session (a window or a monitor).</summary>
/// <remarks>
/// Uses a free-threaded frame pool with two buffers and no FrameArrived handler: the renderer
/// pulls the newest frame when (and only when) it wants one. Not pulling is the throttle —
/// DWM stops producing frames for a pool whose buffers are all in use.
/// </remarks>
internal sealed class CaptureSource : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private SizeInt32? _pendingSize;
    private volatile bool _closed;

    private CaptureSource(long key, IDirect3DDevice device, GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, SizeInt32 size)
    {
        Key = key;
        _device = device;
        _item = item;
        _pool = pool;
        _session = session;
        _poolSize = size;
        _item.Closed += (_, _) => _closed = true;
    }

    public long Key { get; }

    public bool IsClosed => _closed;

    public long LastFrameTimestamp { get; set; }

    public static CaptureSource? Create(long key, GraphicsCaptureItem? item, IDirect3DDevice device, TimeSpan? minUpdateInterval)
    {
        if (item is null) return null;
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) size = new SizeInt32 { Width = 64, Height = 64 };

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        var session = pool.CreateCaptureSession(item);
        // Each property only exists on newer Windows builds; every one is optional.
        try { session.IsCursorCaptureEnabled = false; } catch { /* Windows 10 < 2004 */ }
        try { session.IsBorderRequired = false; } catch { /* Windows 10: yellow border stays */ }
        if (minUpdateInterval is { } interval)
        {
            try { session.MinUpdateInterval = interval; } catch { /* Windows 11 < 24H2 */ }
        }
        session.StartCapture();
        return new CaptureSource(key, device, item, pool, session, size);
    }

    /// <summary>Takes the newest available frame (older queued frames are dropped). Caller disposes it.</summary>
    public Direct3D11CaptureFrame? TryGetLatest()
    {
        Direct3D11CaptureFrame? latest = null;
        for (int i = 0; i < 4; i++)
        {
            var frame = _pool.TryGetNextFrame();
            if (frame is null) break;
            latest?.Dispose();
            latest = frame;
        }
        if (latest is not null)
        {
            var content = latest.ContentSize;
            if (content.Width > 0 && content.Height > 0 && (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
                _pendingSize = content;
        }
        return latest;
    }

    /// <summary>Applies a pending resize once the caller has released the frame.</summary>
    public void AfterFrame()
    {
        if (_pendingSize is not { } size) return;
        _pendingSize = null;
        _poolSize = size;
        try
        {
            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }
        catch
        {
            _closed = true;
        }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* ignore */ }
        try { _pool.Dispose(); } catch { /* ignore */ }
    }
}
