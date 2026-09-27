using System.Collections.Concurrent;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Model;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.WindowManagement;

/// <summary>A decoded app icon (premultiplied BGRA) and the accent colour derived from it.</summary>
internal sealed record IconResult(string AppId, int Width, int Height, byte[] Pixels, ColorF Accent, string? DisplayName);

/// <summary>
/// Loads high-resolution app icons on a background STA thread (shell COM objects want one) and
/// derives each app's glow colour. Results are picked up by the switcher thread.
/// </summary>
internal sealed unsafe class IconService : IDisposable
{
    private const int IconSize = 256;

    private readonly BlockingCollection<(string AppId, string? Path, string? Aumid, long Hwnd)> _requests = new();
    private readonly ConcurrentQueue<IconResult> _results = new();
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _thread;

    public IconService()
    {
        _thread = new Thread(Run) { Name = "FlowSwitch.Icons", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Raised on the icon thread when a result is ready (use it to wake the UI thread).</summary>
    public event Action? ResultReady;

    /// <summary>Queues an icon load once per app (switcher thread).</summary>
    public void Request(WindowInfo window)
    {
        if (!_requested.Add(window.App.Id)) return;
        _requests.Add((window.App.Id, window.App.ExecutablePath, window.App.AppUserModelId, window.Handle));
    }

    public bool TryDequeue(out IconResult result) => _results.TryDequeue(out result!);

    public void Dispose() => _requests.CompleteAdding();

    private void Run()
    {
        CoInitializeEx(0, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    var result = Load(request.AppId, request.Path, request.Aumid, (nint)request.Hwnd);
                    if (result is not null)
                    {
                        _results.Enqueue(result);
                        ResultReady?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Icon load failed for {request.AppId}: {ex.Message}");
                }
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static IconResult? Load(string appId, string? path, string? aumid, nint hwnd)
    {
        string? windowAumid = hwnd != 0 ? ShellApi.GetWindowAppUserModelId(hwnd) : null;
        string? effectiveAumid = aumid ?? windowAumid;
        string? displayName = null;

        nint hbm = 0;
        if (effectiveAumid is not null)
        {
            string parsing = @"shell:AppsFolder\" + effectiveAumid;
            hbm = ShellApi.GetImage(parsing, IconSize);
            if (aumid is not null) displayName = ShellApi.GetDisplayName(parsing);
        }
        if (hbm == 0 && path is not null) hbm = ShellApi.GetImage(path, IconSize);

        byte[]? pixels = null;
        int w = 0, h = 0;
        if (hbm != 0)
        {
            try { pixels = ReadBitmap(hbm, out w, out h); }
            finally { DeleteObject(hbm); }
        }

        if (pixels is null && hwnd != 0)
        {
            nint icon = GetWindowIcon(hwnd);
            if (icon != 0) pixels = ReadIcon(icon, out w, out h);
        }
        if (pixels is null) return null;

        string exe = path is null ? string.Empty : Path.GetFileName(path);
        ColorF accent = KnownAppColors.TryGet(exe, effectiveAumid, out var known)
            ? known
            : AccentExtractor.Extract(pixels, w, h, w * 4, premultiplied: true) ?? ColorF.NeutralAccent;
        return new IconResult(appId, w, h, pixels, AccentExtractor.NormalizeForGlow(accent), displayName);
    }

    private static nint GetWindowIcon(nint hwnd)
    {
        foreach (int kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeoutW(hwnd, WM_GETICON, (nuint)kind, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 60, out nuint result) != 0 && result != 0)
                return (nint)result;
        }
        return GetClassLongPtr(hwnd, GCLP_HICON);
    }

    /// <summary>Reads a 32-bit HBITMAP as top-down premultiplied BGRA.</summary>
    private static byte[]? ReadBitmap(nint hbm, out int width, out int height)
    {
        BITMAP bmp;
        width = height = 0;
        if (GetObjectW(hbm, sizeof(BITMAP), &bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight == 0) return null;
        width = bmp.bmWidth;
        height = Math.Abs(bmp.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        nint dc = GetDC(0);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, hbm, 0, (uint)height, p, &header, 0) == 0) return null;
            }
        }
        finally
        {
            ReleaseDC(0, dc);
        }
        NormalizeAlpha(pixels);
        return pixels;
    }

    private static byte[]? ReadIcon(nint icon, out int width, out int height)
    {
        width = height = 0;
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            return info.hbmColor != 0 ? ReadBitmap(info.hbmColor, out width, out height) : null;
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>
    /// Shell bitmaps are usually premultiplied, legacy icons are sometimes straight alpha or have
    /// no alpha at all. Detect and convert everything to premultiplied.
    /// </summary>
    private static void NormalizeAlpha(byte[] px)
    {
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a != 0) anyAlpha = true;
            if (px[i] > a || px[i + 1] > a || px[i + 2] > a) straight = true;
        }
        if (!anyAlpha)
        {
            for (int i = 0; i < px.Length; i += 4) px[i + 3] = 255;
            return;
        }
        if (!straight) return;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            px[i] = (byte)(px[i] * a / 255);
            px[i + 1] = (byte)(px[i + 1] * a / 255);
            px[i + 2] = (byte)(px[i + 2] * a / 255);
        }
    }
}
