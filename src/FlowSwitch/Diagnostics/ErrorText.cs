using System.ComponentModel;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Diagnostics;
using SharpGen.Runtime;

namespace FlowSwitch.Diagnostics;

/// <summary>Readable descriptions of Win32 errors and HRESULTs for the log and the self-test report.</summary>
internal static class ErrorText
{
    /// <summary>"5 (0x00000005): Access is denied."</summary>
    public static string Win32(int error) =>
        error == 0 ? "0 (no error code set)" : $"{error} (0x{error:X8}): {SafeMessage(() => new Win32Exception(error).Message)}";

    /// <summary>"0x887A0005 DXGI_ERROR_DEVICE_REMOVED: The GPU device instance has been suspended…"</summary>
    public static string HResult(int hr)
    {
        string name = KnownName(hr);
        string message = SafeMessage(() => Marshal.GetExceptionForHR(hr)?.Message ?? string.Empty);
        if ((hr & 0xFFFF0000) == 0x80070000) message = SafeMessage(() => new Win32Exception(hr & 0xFFFF).Message);
        return $"0x{hr:X8}{(name.Length > 0 ? " " + name : string.Empty)}{(message.Length > 0 ? ": " + message : string.Empty)}";
    }

    /// <summary>The HRESULT of an exception (SharpGen/COM/Win32) plus its message.</summary>
    public static string Of(Exception ex) => ex switch
    {
        SharpGenException sg => $"{HResult(sg.ResultCode.Code)} [{ex.GetType().Name}]",
        COMException com => $"{HResult(com.HResult)} [{ex.GetType().Name}]",
        Win32Exception w => $"{Win32(w.NativeErrorCode)} [{ex.GetType().Name}]",
        _ => $"{ex.GetType().FullName}: {ex.Message} (HRESULT 0x{ex.HResult:X8})",
    };

    /// <summary>Logs a failed Win32 call: function, return value, GetLastError and its meaning.</summary>
    public static void LogWin32Failure(string function, object? returned, int lastError, string? context = null) =>
        Log.Error($"{function} failed{(context is null ? string.Empty : " (" + context + ")")}: returned {returned ?? "null"}, " +
                  $"GetLastError = {Win32(lastError)}");

    private static string SafeMessage(Func<string> get)
    {
        try
        {
            return get().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string KnownName(int hr) => unchecked((uint)hr) switch
    {
        0x80004001 => "E_NOTIMPL",
        0x80004002 => "E_NOINTERFACE",
        0x80004005 => "E_FAIL",
        0x80070005 => "E_ACCESSDENIED",
        0x80070057 => "E_INVALIDARG",
        0x8007000E => "E_OUTOFMEMORY",
        0x887A0001 => "DXGI_ERROR_INVALID_CALL",
        0x887A0004 => "DXGI_ERROR_UNSUPPORTED",
        0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED",
        0x887A0006 => "DXGI_ERROR_DEVICE_HUNG",
        0x887A0007 => "DXGI_ERROR_DEVICE_RESET",
        0x887A0020 => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        0x887A0022 => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        0x887A002B => "DXGI_ERROR_ACCESS_DENIED",
        0x88890008 => "DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED",
        0x88890009 => "DCOMPOSITION_ERROR_SURFACE_BEING_RENDERED",
        0x8889000A => "DCOMPOSITION_ERROR_SURFACE_NOT_BEING_RENDERED",
        0x80131040 => "FUSION_E_REF_DEF_MISMATCH (assembly version mismatch)",
        0x80070002 => "ERROR_FILE_NOT_FOUND",
        0x8007007E => "ERROR_MOD_NOT_FOUND",
        _ => string.Empty,
    };
}
