using System.Runtime.InteropServices;

namespace FlowSwitch.Interop;

/// <summary>
/// Minimal raw-vtable COM helpers. FlowSwitch talks to a handful of shell interfaces directly
/// instead of through runtime-generated wrappers: explicit, allocation-free and trimming-safe.
/// </summary>
internal static unsafe class ComInterop
{
    public const uint CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    public static void Release(nint p)
    {
        if (p != 0) Marshal.Release(p);
    }

    /// <summary>Address of vtable slot <paramref name="slot"/> of the COM object <paramref name="p"/>.</summary>
    public static void* Slot(nint p, int slot) => (*(void***)p)[slot];

    public static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0) throw new COMException(what, hr);
    }
}

/// <summary>IShellItemImageFactory / IShellItem / IPropertyStore helpers.</summary>
internal static unsafe class ShellApi
{
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    /// <summary>PKEY_AppUserModel_ID.</summary>
    public static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    public const int SIIGBF_BIGGERSIZEOK = 0x1;
    public const int SIIGBF_ICONONLY = 0x4;
    public const int SIIGBF_SCALEUP = 0x100;
    public const uint SIGDN_NORMALDISPLAY = 0;
    public const ushort VT_LPWSTR = 31;

    /// <summary>Loads the shell's icon for a path or parsing name as a 32-bit HBITMAP (caller deletes).</summary>
    public static nint GetImage(string parsingName, int size)
    {
        int hr = Win32.SHCreateItemFromParsingName(parsingName, 0, IID_IShellItemImageFactory, out nint factory);
        if (hr < 0 || factory == 0) return 0;
        try
        {
            var getImage = (delegate* unmanaged[Stdcall]<nint, SIZE, int, nint*, int>)ComInterop.Slot(factory, 3);
            nint hbm;
            hr = getImage(factory, new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, &hbm);
            return hr >= 0 ? hbm : 0;
        }
        finally
        {
            ComInterop.Release(factory);
        }
    }

    public static string? GetDisplayName(string parsingName)
    {
        int hr = Win32.SHCreateItemFromParsingName(parsingName, 0, IID_IShellItem, out nint item);
        if (hr < 0 || item == 0) return null;
        try
        {
            var getDisplayName = (delegate* unmanaged[Stdcall]<nint, uint, char**, int>)ComInterop.Slot(item, 5);
            char* name;
            hr = getDisplayName(item, SIGDN_NORMALDISPLAY, &name);
            if (hr < 0 || name == null) return null;
            string result = new(name);
            Marshal.FreeCoTaskMem((nint)name);
            return result;
        }
        finally
        {
            ComInterop.Release(item);
        }
    }

    /// <summary>Reads the explicit AppUserModelID a window was tagged with (PWAs, Store app frames).</summary>
    public static string? GetWindowAppUserModelId(nint hwnd)
    {
        int hr = Win32.SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, out nint store);
        if (hr < 0 || store == 0) return null;
        try
        {
            var getValue = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, PROPVARIANT*, int>)ComInterop.Slot(store, 5);
            var key = PKEY_AppUserModel_ID;
            PROPVARIANT value = default;
            hr = getValue(store, &key, &value);
            if (hr < 0) return null;
            string? result = value.vt == VT_LPWSTR && value.pointerValue != 0 ? new string((char*)value.pointerValue) : null;
            Win32.PropVariantClear(ref value);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        finally
        {
            ComInterop.Release(store);
        }
    }
}
