using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace FlowSwitch.Capture;

/// <summary>
/// Bridges Windows.Graphics.Capture to raw Win32 handles and Direct3D textures
/// (IGraphicsCaptureItemInterop / IDirect3DDxgiInterfaceAccess), via explicit vtable calls.
/// </summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static nint s_interop;
    private static readonly object Gate = new();

    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    private static nint Interop
    {
        get
        {
            lock (Gate)
            {
                if (s_interop != 0) return s_interop;
                const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
                int hr = Win32.WindowsCreateString(className, (uint)className.Length, out nint hstring);
                if (hr < 0) return 0;
                try
                {
                    hr = Win32.RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out s_interop);
                    if (hr < 0) Log.Warn($"IGraphicsCaptureItemInterop unavailable: 0x{hr:X8}");
                }
                finally
                {
                    Win32.WindowsDeleteString(hstring);
                }
                return s_interop;
            }
        }
    }

    public static GraphicsCaptureItem? ForWindow(nint hwnd) => Create(hwnd, 3);

    public static GraphicsCaptureItem? ForMonitor(nint monitor) => Create(monitor, 4);

    private static GraphicsCaptureItem? Create(nint handle, int slot)
    {
        nint interop = Interop;
        if (interop == 0) return null;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)ComInterop.Slot(interop, slot);
        Guid iid = IID_IGraphicsCaptureItem;
        nint item;
        int hr = fn(interop, handle, &iid, &item);
        if (hr < 0 || item == 0) return null;
        try
        {
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    /// <summary>Gets the Direct3D texture behind a capture frame surface (caller disposes the wrapper).</summary>
    public static ID3D11Texture2D? GetTexture(IDirect3DSurface surface)
    {
        nint abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        if (abi == 0) return null;
        nint access = 0;
        try
        {
            Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
            if (Marshal.QueryInterface(abi, in accessIid, out access) < 0 || access == 0) return null;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)ComInterop.Slot(access, 3);
            Guid texIid = typeof(ID3D11Texture2D).GUID;
            nint texture;
            return getInterface(access, &texIid, &texture) >= 0 && texture != 0 ? new ID3D11Texture2D(texture) : null;
        }
        finally
        {
            if (access != 0) Marshal.Release(access);
            Marshal.Release(abi);
        }
    }
}
