using System.Collections.Concurrent;
using FlowSwitch.Core.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace FlowSwitch.Capture;

/// <summary>
/// Owns live capture sessions while the overlay is open. Sessions are created and destroyed on
/// a background worker (creation costs a few milliseconds each, which must never land on a
/// frame), and are all stopped when the overlay closes — FlowSwitch captures nothing while idle.
/// </summary>
internal sealed class CaptureService : IDisposable
{
    private readonly IDirect3DDevice? _device;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly ConcurrentQueue<(long Key, bool Monitor, CaptureSource? Source)> _created = new();
    private readonly Dictionary<long, CaptureSource> _windows = new();
    private readonly HashSet<long> _pending = new();
    private readonly HashSet<long> _failed = new();
    private CaptureSource? _monitor;
    private long _monitorKey;
    private bool _monitorPending;

    public CaptureService(IDirect3DDevice? device)
    {
        _device = device;
        Supported = device is not null && CaptureInterop.IsSupported;
        _worker = new Thread(Run) { Name = "FlowSwitch.Capture", IsBackground = true };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        if (Supported) _work.Add(RequestBorderlessAccess);
    }

    public bool Supported { get; }

    public int ActiveCount => _windows.Count + (_monitor is null ? 0 : 1);

    /// <summary>Starts capturing a window if it isn't already (switcher thread).</summary>
    public void EnsureWindow(long hwnd, TimeSpan? minInterval = null)
    {
        if (!Supported || _windows.ContainsKey(hwnd) || _pending.Contains(hwnd) || _failed.Contains(hwnd)) return;
        _pending.Add(hwnd);
        var device = _device!;
        _work.Add(() =>
        {
            CaptureSource? source = null;
            try
            {
                source = CaptureSource.Create(hwnd, CaptureInterop.ForWindow((nint)hwnd), device, minInterval);
            }
            catch (Exception ex)
            {
                Log.Debug($"Window 0x{hwnd:X} cannot be captured: {ex.Message}");
            }
            _created.Enqueue((hwnd, false, source));
        });
    }

    /// <summary>Starts capturing a monitor for the blurred backdrop (switcher thread).</summary>
    public void EnsureMonitor(nint monitor)
    {
        if (!Supported) return;
        if ((_monitor is not null || _monitorPending) && _monitorKey == monitor) return;
        StopMonitor();
        _monitorKey = monitor;
        _monitorPending = true;
        var device = _device!;
        _work.Add(() =>
        {
            CaptureSource? source = null;
            try
            {
                source = CaptureSource.Create(monitor, CaptureInterop.ForMonitor(monitor), device, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"Monitor capture unavailable, using a plain scrim: {ex.Message}");
            }
            _created.Enqueue((monitor, true, source));
        });
    }

    /// <summary>Adopts sessions finished by the worker (switcher thread, once per frame).</summary>
    public void Pump()
    {
        while (_created.TryDequeue(out var item))
        {
            if (item.Monitor)
            {
                if (item.Key == _monitorKey && _monitorPending)
                {
                    _monitor = item.Source;
                    _monitorPending = false;
                }
                else if (item.Source is not null)
                {
                    Retire(item.Source);
                }
                continue;
            }

            if (!_pending.Remove(item.Key))
            {
                if (item.Source is not null) Retire(item.Source);
                continue;
            }
            if (item.Source is null) _failed.Add(item.Key);
            else _windows[item.Key] = item.Source;
        }

        foreach (var (key, source) in _windows.Where(p => p.Value.IsClosed).ToList())
        {
            _windows.Remove(key);
            Retire(source);
        }
    }

    public CaptureSource? Window(long hwnd) => _windows.GetValueOrDefault(hwnd);

    public CaptureSource? Monitor => _monitor;

    /// <summary>Stops every session not in <paramref name="keep"/>.</summary>
    public void StopWindowsExcept(IReadOnlySet<long> keep)
    {
        foreach (var (key, source) in _windows.Where(p => !keep.Contains(p.Key)).ToList())
        {
            _windows.Remove(key);
            Retire(source);
        }
        _pending.RemoveWhere(k => !keep.Contains(k));
    }

    public void StopMonitor()
    {
        if (_monitor is not null) Retire(_monitor);
        _monitor = null;
        _monitorPending = false;
    }

    /// <summary>Called when the overlay closes: no capture keeps running while FlowSwitch is idle.</summary>
    public void StopAll()
    {
        foreach (var source in _windows.Values) Retire(source);
        _windows.Clear();
        _pending.Clear();
        _failed.Clear();
        StopMonitor();
    }

    private void Retire(CaptureSource source) => _work.Add(source.Dispose);

    private void Run()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Debug($"Capture worker: {ex.Message}");
            }
        }
    }

    private static void RequestBorderlessAccess()
    {
        try
        {
            // Windows 11: unpackaged apps are allowed borderless capture; asking once makes it explicit.
            var op = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            op.AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Older Windows: a yellow capture border may appear around captured windows.
        }
    }

    public void Dispose()
    {
        StopAll();
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
    }
}
