using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Diagnostics;
using Windows.Graphics.DirectX.Direct3D11;

namespace FlowSwitch.Capture;

/// <summary>
/// Every entry point into Windows.Graphics.Capture from code that must keep working without it.
/// All methods are non-inlined and exception-safe: if the WinRT projection
/// (Microsoft.Windows.SDK.NET.dll) cannot load, the failure surfaces here as an error string —
/// never as a type-load exception inside the renderer or the switcher.
/// </summary>
internal static class CaptureProbe
{
    /// <summary>Creates the capture service, or returns null with the reason.</summary>
    public static CaptureService? TryCreateService(object? winrtDevice, out string? error)
    {
        error = null;
        if (winrtDevice is null)
        {
            error = "no WinRT Direct3D device";
            return null;
        }
        try
        {
            return CreateServiceCore(winrtDevice, out error);
        }
        catch (Exception ex)
        {
            error = $"Windows.Graphics.Capture unavailable: {ErrorText.Of(ex)}";
            Log.Error("Creating the capture service failed", ex);
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CaptureService? CreateServiceCore(object winrtDevice, out string? error)
    {
        var service = new CaptureService((IDirect3DDevice)winrtDevice);
        error = service.Supported ? null : "GraphicsCaptureSession.IsSupported() returned false";
        return service;
    }

    /// <summary>
    /// Checks that the WinRT projection this build was compiled against is the one that loads.
    /// Returns a description; <paramref name="broken"/> is set when it cannot load or is older.
    /// </summary>
    public static string CheckProjection(out bool broken)
    {
        broken = false;
        var expected = typeof(CaptureProbe).Assembly.GetReferencedAssemblies()
            .FirstOrDefault(a => a.Name == "Microsoft.Windows.SDK.NET");
        string expectedText = expected?.Version?.ToString() ?? "?";
        try
        {
            var loaded = LoadedProjection();
            string location = string.IsNullOrEmpty(loaded.Location) ? "?" : loaded.Location;
            var version = loaded.GetName().Version;
            if (expected?.Version is { } e && version is not null && version < e)
            {
                broken = true;
                return $"Microsoft.Windows.SDK.NET {version} loaded, but FlowSwitch needs {e} ({location})";
            }
            return $"Microsoft.Windows.SDK.NET {version} (expected {expectedText}) from {location}";
        }
        catch (Exception ex)
        {
            broken = true;
            return $"Microsoft.Windows.SDK.NET {expectedText} cannot be loaded: {ErrorText.Of(ex)}";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadedProjection() => typeof(Windows.Graphics.Capture.GraphicsCaptureSession).Assembly;

    /// <summary>
    /// Self-test: captures the given monitor and waits for one real frame. Runs on the calling
    /// (background) thread; the frame pool is free-threaded.
    /// </summary>
    public static bool TryCaptureFrame(object? winrtDevice, nint monitor, TimeSpan timeout, out string detail)
    {
        if (winrtDevice is null)
        {
            detail = "no WinRT Direct3D device";
            return false;
        }
        try
        {
            return TryCaptureFrameCore(winrtDevice, monitor, timeout, out detail);
        }
        catch (Exception ex)
        {
            detail = ErrorText.Of(ex);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryCaptureFrameCore(object winrtDevice, nint monitor, TimeSpan timeout, out string detail)
    {
        if (!CaptureInterop.IsSupported)
        {
            detail = "GraphicsCaptureSession.IsSupported() = false";
            return false;
        }
        var item = CaptureInterop.ForMonitor(monitor);
        if (item is null)
        {
            detail = "IGraphicsCaptureItemInterop.CreateForMonitor failed";
            return false;
        }
        using var source = CaptureSource.Create(monitor, item, (IDirect3DDevice)winrtDevice, null);
        if (source is null)
        {
            detail = "capture session could not be created";
            return false;
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var frame = source.TryGetLatest();
            if (frame is not null)
            {
                var size = frame.ContentSize;
                detail = $"first monitor frame {size.Width}×{size.Height} after {sw.ElapsedMilliseconds} ms";
                return true;
            }
            Thread.Sleep(15);
        }
        detail = $"capture started but no frame within {timeout.TotalMilliseconds:0} ms";
        return false;
    }
}
