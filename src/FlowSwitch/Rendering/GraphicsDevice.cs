using System.Runtime.CompilerServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Diagnostics;
using FlowSwitch.Interop;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using WinRT;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;

namespace FlowSwitch.Rendering;

/// <summary>
/// One Direct3D 11 device shared by the overlay renderer, Direct2D/DirectWrite text rendering,
/// DirectComposition and Windows.Graphics.Capture.
/// </summary>
/// <remarks>
/// Every creation step is checked and logged by name; the first failure throws a
/// <see cref="GraphicsInitException"/> naming the stage. Windows.Graphics.Capture is optional: its
/// WinRT device is created in an isolated method, so a broken or missing WinRT projection costs
/// live previews, never the switcher itself.
/// </remarks>
internal sealed class GraphicsDevice : IDisposable
{
    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, bool isWarp)
    {
        Device = device;
        Context = context;
        IsWarp = isWarp;

        // Capture frame pools touch the device from worker threads.
        using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        DxgiDevice = Stage("IDXGIDevice", () => device.QueryInterface<IDXGIDevice>());
        using (var adapter = Stage("IDXGIDevice.GetAdapter", () => DxgiDevice.GetAdapter()))
        {
            var d = adapter.Description;
            AdapterName = $"{d.Description.TrimEnd('\0')} (vendor 0x{d.VendorId:X4}, device 0x{d.DeviceId:X4}, {d.DedicatedVideoMemory / (1024 * 1024)} MB)";
            Factory = Stage("DXGI factory (IDXGIFactory2)", () => adapter.GetParent<IDXGIFactory2>());
        }
        Log.Info($"DXGI adapter: {AdapterName}{(isWarp ? " [WARP software renderer]" : string.Empty)}.");
        HostHealth.Adapter = AdapterName + (isWarp ? " [WARP]" : string.Empty);
        HostHealth.D3D = ComponentState.Ready;

        HostHealth.Composition = ComponentState.Starting;
        try
        {
            Composition = Stage("DCompositionCreateDevice", () => DComp.DCompositionCreateDevice<IDCompositionDevice>(DxgiDevice));
            Log.Info("DirectComposition device created.");
        }
        catch (GraphicsInitException ex)
        {
            // Not fatal: the overlay falls back to a plain (opaque) window swap chain.
            HostHealth.Composition = ComponentState.Failed;
            CompositionError = ex.Message;
            Log.Error($"{ex.Message} — the overlay will use an opaque window swap chain instead of DirectComposition.");
        }

        D2DFactory = Stage("D2D1CreateFactory", () => D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded));
        D2DDevice = Stage("ID2D1Factory1.CreateDevice", () => D2DFactory.CreateDevice(DxgiDevice));
        D2DContext = Stage("ID2D1Device.CreateDeviceContext", () => D2DDevice.CreateDeviceContext(DeviceContextOptions.None));
        DWrite = Stage("DWriteCreateFactory", () => Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared));
        Log.Info("Direct2D / DirectWrite ready.");
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIDevice DxgiDevice { get; }
    public IDXGIFactory2 Factory { get; }
    /// <summary>DirectComposition device, or null when unavailable (the overlay then uses a window swap chain).</summary>
    public IDCompositionDevice? Composition { get; }

    public string? CompositionError { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public ID2D1DeviceContext D2DContext { get; }
    public IDWriteFactory DWrite { get; }
    public string AdapterName { get; } = "unknown";
    public bool IsWarp { get; }

    /// <summary>
    /// The Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice for capture, or null. Typed as
    /// object on purpose: this class must load even when the WinRT projection cannot.
    /// </summary>
    public object? WinRTDevice { get; private set; }

    public bool IsLost => Device.DeviceRemovedReason.Failure;

    public string DeviceRemovedReason => ErrorText.HResult(Device.DeviceRemovedReason.Code);

    public static GraphicsDevice Create()
    {
        HostHealth.D3D = ComponentState.Starting;
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Success)
        {
            Log.Info($"D3D11CreateDevice(hardware) succeeded: feature level {device.FeatureLevel}.");
            return Wrap(device, context, false);
        }

        Log.Warn($"D3D11CreateDevice(hardware) failed: {ErrorText.HResult(result.Code)} — trying WARP (software).");
        result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, levels, out device, out context);
        if (result.Failure) HostHealth.D3D = ComponentState.Failed;
        if (result.Failure) throw new GraphicsInitException("D3D11CreateDevice", $"hardware and WARP both failed; WARP: {ErrorText.HResult(result.Code)}");
        Log.Info($"D3D11CreateDevice(WARP) succeeded: feature level {device.FeatureLevel}.");
        return Wrap(device, context, true);
    }

    private static GraphicsDevice Wrap(ID3D11Device device, ID3D11DeviceContext context, bool isWarp)
    {
        if (device.FeatureLevel < FeatureLevel.Level_11_0)
        {
            string level = device.FeatureLevel.ToString();
            context.Dispose();
            device.Dispose();
            HostHealth.D3D = ComponentState.Failed;
            throw new GraphicsInitException("D3D11 feature level", $"FlowSwitch's shaders need Direct3D feature level 11_0; this GPU/driver offers {level}");
        }
        try
        {
            return new GraphicsDevice(device, context, isWarp);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the WinRT device used by Windows.Graphics.Capture. Never throws: failure only
    /// disables live previews and the blurred backdrop.
    /// </summary>
    public bool TryCreateWinRTDevice(out string? error)
    {
        error = null;
        if (WinRTDevice is not null) return true;
        try
        {
            WinRTDevice = CreateWinRTDeviceCore(DxgiDevice.NativePointer, out error);
            return WinRTDevice is not null;
        }
        catch (Exception ex)
        {
            // Typically FileLoadException: Microsoft.Windows.SDK.NET.dll is missing or the wrong version.
            error = $"WinRT interop unavailable: {ErrorText.Of(ex)}";
            Log.Error("Creating the WinRT Direct3D device failed (no live previews)", ex);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? CreateWinRTDeviceCore(nint dxgiDevice, out string? error)
    {
        error = null;
        int hr = Win32.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable);
        if (hr < 0 || inspectable == 0)
        {
            error = $"CreateDirect3D11DeviceFromDXGIDevice failed: {ErrorText.HResult(hr)}";
            Log.Error(error);
            return null;
        }
        try
        {
            return MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(inspectable);
        }
    }

    /// <summary>Runs one creation step; a failure becomes a <see cref="GraphicsInitException"/> naming the step.</summary>
    public static T Stage<T>(string stage, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (GraphicsInitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GraphicsInitException(stage, ErrorText.Of(ex), ex);
        }
    }

    /// <summary>Checks an HRESULT-returning call; a failure becomes a <see cref="GraphicsInitException"/>.</summary>
    public static void Check(string stage, Result result)
    {
        if (result.Failure) throw new GraphicsInitException(stage, ErrorText.HResult(result.Code));
    }

    /// <summary>Releases memory the driver keeps for idle apps (called after the overlay hides).</summary>
    public void Trim()
    {
        try
        {
            using var dxgi3 = Device.QueryInterfaceOrNull<IDXGIDevice3>();
            dxgi3?.Trim();
        }
        catch
        {
            // optional
        }
    }

    public void Dispose()
    {
        (WinRTDevice as IDisposable)?.Dispose();
        DWrite.Dispose();
        D2DContext.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        Composition?.Dispose();
        Factory.Dispose();
        DxgiDevice.Dispose();
        Context.ClearState();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>A graphics setup step failed. <see cref="Stage"/> names it, the message carries the HRESULT.</summary>
internal sealed class GraphicsInitException : Exception
{
    public GraphicsInitException(string stage, string detail, Exception? inner = null)
        : base($"{stage} failed: {detail}", inner)
    {
        Stage = stage;
        Detail = detail;
    }

    public string Stage { get; }
    public string Detail { get; }
}
