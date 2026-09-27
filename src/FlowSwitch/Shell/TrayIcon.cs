using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Shell;

internal enum TrayCommand
{
    None = 0,
    OpenSettings = 100,
    Preview = 101,
    ToggleEnabled = 102,
    Pause = 103,
    Resume = 104,
    Restart = 105,
    Exit = 106,
}

/// <summary>The notification-area icon and its menu. Lives on the main (host) thread.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    public const uint CallbackMessage = WM_APP + 100;

    private readonly nint _owner;
    private nint _icon;
    private nint _pausedIcon;
    private bool _added;
    private bool _paused;

    public TrayIcon(nint owner)
    {
        _owner = owner;
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        _icon = IconLoader.Load("FlowSwitch.Assets.FlowSwitch.ico", size);
        _pausedIcon = IconLoader.Load("FlowSwitch.Assets.FlowSwitch-paused.ico", size);
        EnableDarkMenus();
    }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset? PausedUntil { get; set; }

    public void Show() => Update(add: !_added);

    public void SetState(bool enabled, bool paused, string tooltip)
    {
        Enabled = enabled;
        _paused = paused || !enabled;
        _tooltip = tooltip;
        Update(add: !_added);
    }

    private string _tooltip = "FlowSwitch";

    /// <summary>Re-adds the icon after Explorer restarts (TaskbarCreated).</summary>
    public void Recreate()
    {
        _added = false;
        Update(add: true);
    }

    private void Update(bool add)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _owner,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _paused && _pausedIcon != 0 ? _pausedIcon : _icon,
        };
        CopyString(_tooltip, data.szTip, 128);
        if (add)
        {
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            if (_added) Log.Info($"Tray icon added (icon handle 0x{data.hIcon:X}).");
            else Log.Warn($"Shell_NotifyIcon(NIM_ADD) failed (Explorer not ready yet?) — will retry when the taskbar is created. " +
                          $"GetLastError = {FlowSwitch.Diagnostics.ErrorText.Win32(System.Runtime.InteropServices.Marshal.GetLastSystemError())}");
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _owner,
            uID = IconId,
            uFlags = NIF_INFO,
            dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_NOSOUND,
        };
        CopyString(text, data.szInfo, 256);
        CopyString(title, data.szInfoTitle, 64);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Handles the callback message. Returns the command chosen, if any.</summary>
    public TrayCommand HandleCallback(nuint wParam, nint lParam)
    {
        uint evt = (uint)(lParam & 0xFFFF);
        // NOTIFYICON_VERSION_4: a click arrives as NIN_SELECT, a right click / Shift+F10 as WM_CONTEXTMENU,
        // with the anchor point in wParam.
        switch (evt)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                return TrayCommand.OpenSettings;
            case WM_CONTEXTMENU:
                int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
                return ShowMenu(x, y);
            default:
                return TrayCommand.None;
        }
    }

    private TrayCommand ShowMenu(int x, int y)
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.OpenSettings, "Open Settings");
            AppendMenuW(menu, MF_STRING | (Enabled ? 0 : MF_GRAYED), (nuint)TrayCommand.Preview, "Preview switcher");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING | (Enabled ? MF_CHECKED : 0), (nuint)TrayCommand.ToggleEnabled, "Enable FlowSwitch");
            if (PausedUntil is { } until && until > DateTimeOffset.Now)
                AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Resume, $"Resume (paused until {until.LocalDateTime:t})");
            else
                AppendMenuW(menu, MF_STRING | (Enabled ? 0 : MF_GRAYED), (nuint)TrayCommand.Pause, "Pause FlowSwitch for 1 hour");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Restart, "Restart");
            AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Exit, "Exit");

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_owner);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, _owner, 0);
            PostMessageW(_owner, WM_NULL, 0, 0);
            return (TrayCommand)cmd;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void CopyString(string value, char* destination, int capacity)
    {
        int n = Math.Min(value.Length, capacity - 1);
        for (int i = 0; i < n; i++) destination[i] = value[i];
        destination[n] = '\0';
    }

    /// <summary>Lets Win32 popup menus follow the Windows dark/light app theme (uxtheme ordinals 135/136).</summary>
    private static void EnableDarkMenus()
    {
        try
        {
            nint uxtheme = LoadLibraryW("uxtheme.dll");
            if (uxtheme == 0) return;
            nint setPreferredAppMode = GetProcAddress(uxtheme, 135);
            nint flushMenuThemes = GetProcAddress(uxtheme, 136);
            if (setPreferredAppMode != 0) ((delegate* unmanaged[Stdcall]<int, int>)setPreferredAppMode)(1); // AllowDark
            if (flushMenuThemes != 0) ((delegate* unmanaged[Stdcall]<void>)flushMenuThemes)();
        }
        catch (Exception ex)
        {
            Log.Debug($"Dark menus unavailable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _owner, uID = IconId };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0) DestroyIcon(_icon);
        if (_pausedIcon != 0) DestroyIcon(_pausedIcon);
        _icon = _pausedIcon = 0;
    }
}
