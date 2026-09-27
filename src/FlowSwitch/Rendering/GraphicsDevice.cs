using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
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
using IDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace FlowSwitch.Rendering;

/// <summary>
/// One Direct3D 11 device shared by the overlay renderer, Direct2D/DirectWrite text rendering,
/// DirectComposition and Windows.Graphics.Capture.
/// </summary>
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

        DxgiDevice = device.QueryInterface<IDXGIDevice>();
        using (var adapter = DxgiDevice.GetAdapter())
            Factory = adapter.GetParent<IDXGIFactory2>();

        Composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(DxgiDevice);

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        D2DDevice = D2DFactory.CreateDevice(DxgiDevice);
        D2DContext = D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);

        WinRTDevice = CreateWinRTDevice(DxgiDevice);
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIDevice DxgiDevice { get; }
    public IDXGIFactory2 Factory { get; }
    public IDCompositionDevice Composition { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public ID2D1DeviceContext D2DContext { get; }
    public IDWriteFactory DWrite { get; }
    public IDirect3DDevice? WinRTDevice { get; }
    public bool IsWarp { get; }

    public bool IsLost => Device.DeviceRemovedReason.Failure;

    public static GraphicsDevice Create()
    {
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Success)
        {
            Log.Info($"Direct3D 11 device created (feature level {device.FeatureLevel}).");
            return new GraphicsDevice(device, context, false);
        }

        Log.Warn($"Hardware device unavailable ({result}); falling back to WARP.");
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, levels, out device, out context).CheckError();
        return new GraphicsDevice(device, context, true);
    }

    private static IDirect3DDevice? CreateWinRTDevice(IDXGIDevice dxgi)
    {
        try
        {
            int hr = Win32.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint inspectable);
            if (hr < 0 || inspectable == 0) return null;
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(inspectable);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WinRT Direct3D device unavailable (no live previews): {ex.Message}");
            return null;
        }
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
        Composition.Dispose();
        Factory.Dispose();
        DxgiDevice.Dispose();
        Context.ClearState();
        Context.Dispose();
        Device.Dispose();
    }
}
